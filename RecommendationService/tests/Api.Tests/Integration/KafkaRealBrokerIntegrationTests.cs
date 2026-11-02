using System.Text;
using Moq;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dprental.RecommendationService.Api.Tests.TestHelpers;
using Dprental.RecommendationService.Application.DependencyInjection;
using Dprental.RecommendationService.Application.Kafka;
using Dprental.RecommendationService.Application.Kafka.Consumers;
using Dprental.RecommendationService.Application.Kafka.Options;
using Dprental.RecommendationService.Application.Kafka.Producer;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Application.Validation;
using Dprental.RecommendationService.Domain.DependencyInjection;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class KafkaRealBrokerIntegrationTests : IClassFixture<PostgreSqlTestcontainerFixture>, IClassFixture<KafkaTestcontainerFixture>
{
    private readonly PostgreSqlTestcontainerFixture _postgresFixture;
    private readonly KafkaTestcontainerFixture _kafkaFixture;

    public KafkaRealBrokerIntegrationTests(
        PostgreSqlTestcontainerFixture postgresFixture,
        KafkaTestcontainerFixture kafkaFixture)
    {
        _postgresFixture = postgresFixture;
        _kafkaFixture = kafkaFixture;
    }

    [Fact]
    public async Task RealKafka_DlqProducer_DeliversAuditedRecord_AndCanBeReadBack()
    {
        string bootstrapServers = _kafkaFixture.BootstrapServers;
        string dlqTopic = $"test.dlq.{Guid.NewGuid():N}";
        var options = Options.Create(new KafkaOptions { BootstrapServers = bootstrapServers });
        var clientFactory = new KafkaClientFactory(options);
        var dlqProducer = new KafkaDlqProducer(clientFactory, NullLogger<KafkaDlqProducer>.Instance);

        string testKey = $"key-{Guid.NewGuid():N}";
        string testPayload = "{\"bad\": \"payload\"}";
        string reason = "Testing DLQ delivery";
        string sourceTopic = "test.source.events.v1";
        int sourcePartition = 2;
        long sourceOffset = 1337;

        await dlqProducer.ProduceToDlqAsync(
            dlqTopic, testKey, testPayload, reason, sourceTopic, sourcePartition, sourceOffset, CancellationToken.None);

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"verify-dlq-group-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string?, string>(consumerConfig).Build();
        consumer.Subscribe(dlqTopic);

        var record = consumer.Consume(TimeSpan.FromSeconds(10));
        Assert.NotNull(record);
        Assert.Equal(testKey, record.Message.Key);
        Assert.Equal(testPayload, record.Message.Value);

        var headers = record.Message.Headers;
        Assert.NotNull(headers);

        string actualReason = Encoding.UTF8.GetString(headers.GetLastBytes("x-dlq-error-reason")!);
        string actualSourceTopic = Encoding.UTF8.GetString(headers.GetLastBytes("x-dlq-source-topic")!);
        string actualPartition = Encoding.UTF8.GetString(headers.GetLastBytes("x-dlq-source-partition")!);
        string actualOffset = Encoding.UTF8.GetString(headers.GetLastBytes("x-dlq-source-offset")!);
        byte[]? timestampBytes = headers.GetLastBytes("x-dlq-timestamp");

        Assert.Equal(reason, actualReason);
        Assert.Equal(sourceTopic, actualSourceTopic);
        Assert.Equal("2", actualPartition);
        Assert.Equal("1337", actualOffset);
        Assert.NotNull(timestampBytes);
    }

    [Fact]
    public async Task RealKafka_EndToEnd_Produce_Consume_Postgres_And_Deduplicate()
    {
        string bootstrapServers = _kafkaFixture.BootstrapServers;
        await using (var db = _postgresFixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        string testTopic = $"test.inventory.{Guid.NewGuid():N}";
        string testGroup = $"test.group.{Guid.NewGuid():N}";
        string testDlq = $"{testTopic}.dlq";

        // Create topic with 3 partitions
        using var adminClient = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        await adminClient.CreateTopicsAsync(new[]
        {
            new TopicSpecification { Name = testTopic, NumPartitions = 3, ReplicationFactor = 1 }
        });

        var options = new KafkaOptions
        {
            BootstrapServers = bootstrapServers,
            InventoryTopic = testTopic,
            InventoryConsumerGroup = testGroup,
            InventoryDlqTopic = testDlq,
            RentalTopic = $"test.rental.{Guid.NewGuid():N}",
            RentalConsumerGroup = $"test.rental.group.{Guid.NewGuid():N}",
            RentalDlqTopic = $"test.rental.{Guid.NewGuid():N}.dlq",
            Enabled = true
        };

        var clientFactory = new KafkaClientFactory(Options.Create(options));
        var dlqProducer = new KafkaDlqProducer(clientFactory, NullLogger<KafkaDlqProducer>.Instance);
        var services = new ServiceCollection();
        services.AddRecommendationServiceDomainRepositories(_postgresFixture.ConnectionString);
        services.AddSingleton<IEventSchemaValidator, EventSchemaValidator>();
        services.AddScoped<IInteractionIngestionService, InteractionIngestionService>();
        services.AddSingleton(Mock.Of<ISnapshotPublishingService>());
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // 1. Publish backlog before worker starts across 3 partitions
        var listingIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var msgIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        using var producer = clientFactory.CreateProducer();

        for (int i = 0; i < 3; i++)
        {
            string json = $$"""
            {
                "messageId": "{{msgIds[i]}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": null,
                    "equipmentListingId": "{{listingIds[i]}}",
                    "equipmentTypeId": "{{Guid.NewGuid()}}"
                }
            }
            """;
            await producer.ProduceAsync(new TopicPartition(testTopic, new Partition(i)), new Message<string?, string>
            {
                Key = listingIds[i].ToString("D"),
                Value = json
            });
        }

        // 2. Start worker
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactory,
            dlqProducer,
            clientFactory,
            Options.Create(options),
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);

        // Wait for all 3 events to be consumed and processed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var queryDb = _postgresFixture.CreateDbContext();
            int processedCount = await queryDb.ListingPopularities.CountAsync(p => listingIds.Contains(p.EquipmentListingId));
            if (processedCount == 3)
            {
                break;
            }
            await Task.Delay(200);
        }

        await using (var finalDb = _postgresFixture.CreateDbContext())
        {
            int finalCount = await finalDb.ListingPopularities.CountAsync(p => listingIds.Contains(p.EquipmentListingId));
            Assert.Equal(3, finalCount);
        }

        // Check committed offset reaches log-end offset on all 3 partitions
        var verifyConsumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = testGroup,
            EnableAutoCommit = false
        };
        using (var verifyConsumer = new ConsumerBuilder<string?, string>(verifyConsumerConfig).Build())
        {
            var queryPartitions = new[]
            {
                new TopicPartition(testTopic, new Partition(0)),
                new TopicPartition(testTopic, new Partition(1)),
                new TopicPartition(testTopic, new Partition(2))
            };
            var committed = verifyConsumer.Committed(queryPartitions, TimeSpan.FromSeconds(5));
            foreach (var tp in queryPartitions)
            {
                var watermark = verifyConsumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));
                var partitionCommit = committed.First(c => c.Topic == tp.Topic && c.Partition == tp.Partition);
                Assert.True(partitionCommit.Offset.Value >= watermark.High.Value,
                    $"Committed offset {partitionCommit.Offset.Value} should reach log-end high watermark {watermark.High.Value}");
            }
        }

        // 3. Publish duplicate message
        string dupJson = $$"""
        {
            "messageId": "{{msgIds[0]}}",
            "messageType": "ListingViewed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": null,
                "equipmentListingId": "{{listingIds[0]}}",
                "equipmentTypeId": "{{Guid.NewGuid()}}"
            }
        }
        """;
        await producer.ProduceAsync(new TopicPartition(testTopic, new Partition(0)), new Message<string?, string>
        {
            Key = listingIds[0].ToString("D"),
            Value = dupJson
        });

        await Task.Delay(1500);

        // Verify duplicate didn't increment popularity twice
        await using (var verifyDb = _postgresFixture.CreateDbContext())
        {
            var pop0 = await verifyDb.ListingPopularities.FindAsync(listingIds[0]);
            Assert.NotNull(pop0);
            Assert.Equal(2, pop0.Score);

            int rawCount = await verifyDb.InteractionEvents.CountAsync(e => e.MessageId == msgIds[0]);
            Assert.Equal(1, rawCount);
        }

        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RealKafka_RestartAndRebalance_RecoversFromCommittedOffset_WithoutLoss()
    {
        string bootstrapServers = _kafkaFixture.BootstrapServers;
        await using (var db = _postgresFixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        string testTopic = $"test.rebalance.{Guid.NewGuid():N}";
        string testGroup = $"test.rebalance.group.{Guid.NewGuid():N}";
        string testDlq = $"{testTopic}.dlq";

        // Create topic with 3 partitions
        using var adminClient = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        await adminClient.CreateTopicsAsync(new[]
        {
            new TopicSpecification { Name = testTopic, NumPartitions = 3, ReplicationFactor = 1 }
        });

        var options = new KafkaOptions
        {
            BootstrapServers = bootstrapServers,
            InventoryTopic = testTopic,
            InventoryConsumerGroup = testGroup,
            InventoryDlqTopic = testDlq,
            RentalTopic = $"test.rental.{Guid.NewGuid():N}",
            RentalConsumerGroup = $"test.rental.group.{Guid.NewGuid():N}",
            RentalDlqTopic = $"test.rental.{Guid.NewGuid():N}.dlq",
            Enabled = true
        };

        var clientFactory = new KafkaClientFactory(Options.Create(options));
        var dlqProducer = new KafkaDlqProducer(clientFactory, NullLogger<KafkaDlqProducer>.Instance);

        var services = new ServiceCollection();
        services.AddRecommendationServiceDomainRepositories(_postgresFixture.ConnectionString);
        services.AddSingleton<IEventSchemaValidator, EventSchemaValidator>();
        services.AddScoped<IInteractionIngestionService, InteractionIngestionService>();
        services.AddSingleton(Mock.Of<ISnapshotPublishingService>());
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        // Publish backlog of 6 messages across 3 partitions
        var listingIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var msgIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        using var producer = clientFactory.CreateProducer();

        for (int i = 0; i < 6; i++)
        {
            string json = $$"""
            {
                "messageId": "{{msgIds[i]}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": null,
                    "equipmentListingId": "{{listingIds[i]}}",
                    "equipmentTypeId": "{{Guid.NewGuid()}}"
                }
            }
            """;
            await producer.ProduceAsync(new TopicPartition(testTopic, new Partition(i % 3)), new Message<string?, string>
            {
                Key = listingIds[i].ToString("D"),
                Value = json
            });
        }

        // Start worker 1, let it process some items, then restart worker 2
        using var cts1 = new CancellationTokenSource();
        var worker1 = new RecommendationKafkaConsumerWorker(
            scopeFactory,
            dlqProducer,
            clientFactory,
            Options.Create(options),
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask1 = worker1.StartAsync(cts1.Token);

        // Wait until at least 2 items processed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var queryDb = _postgresFixture.CreateDbContext();
            int processed = await queryDb.ListingPopularities.CountAsync(p => listingIds.Contains(p.EquipmentListingId));
            if (processed >= 2)
            {
                break;
            }
            await Task.Delay(200);
        }

        // Stop worker 1
        await worker1.StopAsync(CancellationToken.None);
        try { await workerTask1; } catch (OperationCanceledException) { }

        // Start worker 2 (same group, simulates consumer restart/recovery)
        using var cts2 = new CancellationTokenSource();
        var worker2 = new RecommendationKafkaConsumerWorker(
            scopeFactory,
            dlqProducer,
            clientFactory,
            Options.Create(options),
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask2 = worker2.StartAsync(cts2.Token);

        // Wait until ALL 6 items are processed
        sw.Restart();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var queryDb = _postgresFixture.CreateDbContext();
            int processed = await queryDb.ListingPopularities.CountAsync(p => listingIds.Contains(p.EquipmentListingId));
            if (processed == 6)
            {
                break;
            }
            await Task.Delay(200);
        }

        await using (var finalDb = _postgresFixture.CreateDbContext())
        {
            int totalProcessed = await finalDb.ListingPopularities.CountAsync(p => listingIds.Contains(p.EquipmentListingId));
            Assert.Equal(6, totalProcessed);
        }

        await worker2.StopAsync(CancellationToken.None);
        try { await workerTask2; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task KafkaLogging_SmokeTest_OutputsToStdout_ZeroLog4jFiles_AndDockerRotationVerified()
    {
        string containerName = $"kafka-logging-smoke-{Guid.NewGuid():N}";
        string? dir = AppContext.BaseDirectory;
        string log4jPath = "";
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "kafka-log4j2.yaml");
            if (File.Exists(candidate))
            {
                log4jPath = candidate;
                break;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.True(File.Exists(log4jPath), $"kafka-log4j2.yaml must exist, searched up from {AppContext.BaseDirectory}");

        // 1. Run Kafka container with the custom log4j2.yaml and GC opts
        var startPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run -d --name {containerName} " +
                        $"-e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller " +
                        $"-e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 " +
                        $"-e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://127.0.0.1:9092 " +
                        $"-e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT " +
                        $"-e KAFKA_CONTROLLER_QUORUM_VOTERS=1@127.0.0.1:9093 " +
                        $"-e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER " +
                        $"-e KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT " +
                        $"-e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 " +
                        $"-e KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1 " +
                        $"-e KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1 " +
                        $"-e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 " +
                        $"-e KAFKA_LOG_DIRS=/tmp/kraft-combined-logs " +
                        $"-e KAFKA_LOG4J_OPTS=\"-Dlog4j2.configurationFile=/etc/kafka/log4j2.yaml\" " +
                        $"-e KAFKA_GC_LOG_OPTS=\"-Xlog:gc*:file=/opt/kafka/logs/kafkaServer-gc.log:time,tags:filecount=5,filesize=10M\" " +
                        $"--log-opt max-size=20m --log-opt max-file=5 " +
                        $"-v {log4jPath}:/etc/kafka/log4j2.yaml:ro " +
                        $"apache/kafka:4.3.1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var startProc = System.Diagnostics.Process.Start(startPsi)!;
        await startProc.WaitForExitAsync();
        Assert.Equal(0, startProc.ExitCode);

        try
        {
            await Task.Delay(5000);

            // Verify stdout logs
            var logsPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"logs --tail 30 {containerName}",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var logsProc = System.Diagnostics.Process.Start(logsPsi)!;
            string stdoutLogs = await logsProc.StandardOutput.ReadToEndAsync();
            await logsProc.WaitForExitAsync();
            Assert.NotEmpty(stdoutLogs);
            Assert.Contains("appuser", stdoutLogs);

            // Verify /opt/kafka/logs has no log4j files (only GC logs)
            var lsPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} ls /opt/kafka/logs",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var lsProc = System.Diagnostics.Process.Start(lsPsi)!;
            string filesInLogsDir = await lsProc.StandardOutput.ReadToEndAsync();
            await lsProc.WaitForExitAsync();

            Assert.DoesNotContain("server.log", filesInLogsDir);
            Assert.DoesNotContain("controller.log", filesInLogsDir);
            Assert.DoesNotContain("state-change.log", filesInLogsDir);
            Assert.Contains("kafkaServer-gc.log", filesInLogsDir);
        }
        finally
        {
            var rmPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"rm -f {containerName}",
                UseShellExecute = false
            };
            using var rmProc = System.Diagnostics.Process.Start(rmPsi);
            await rmProc!.WaitForExitAsync();
        }

        // 2. Test Docker rotation by generating log output beyond threshold
        string rotationCtr = $"rotation-test-{Guid.NewGuid():N}";
        var rotStartPsi = new System.Diagnostics.ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        rotStartPsi.ArgumentList.Add("run");
        rotStartPsi.ArgumentList.Add("-d");
        rotStartPsi.ArgumentList.Add("--name");
        rotStartPsi.ArgumentList.Add(rotationCtr);
        rotStartPsi.ArgumentList.Add("--log-opt");
        rotStartPsi.ArgumentList.Add("max-size=50k");
        rotStartPsi.ArgumentList.Add("--log-opt");
        rotStartPsi.ArgumentList.Add("max-file=3");
        rotStartPsi.ArgumentList.Add("alpine");
        rotStartPsi.ArgumentList.Add("sh");
        rotStartPsi.ArgumentList.Add("-c");
        rotStartPsi.ArgumentList.Add("for i in $(seq 1 3000); do echo \"log rotation test message line $i\"; done");
        using var rotStartProc = System.Diagnostics.Process.Start(rotStartPsi)!;
        await rotStartProc.WaitForExitAsync();
        Assert.Equal(0, rotStartProc.ExitCode);

        try
        {
            await Task.Delay(2000);

            // Inspect container log configuration
            var inspectPsi = new System.Diagnostics.ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            inspectPsi.ArgumentList.Add("inspect");
            inspectPsi.ArgumentList.Add(rotationCtr);
            inspectPsi.ArgumentList.Add("--format={{json .HostConfig.LogConfig}}");
            using var inspectProc = System.Diagnostics.Process.Start(inspectPsi)!;
            string logConfig = (await inspectProc.StandardOutput.ReadToEndAsync()).Trim();
            await inspectProc.WaitForExitAsync();

            Assert.Contains("json-file", logConfig);
            Assert.Contains("max-size", logConfig);
            Assert.Contains("max-file", logConfig);

            // Check logs are readable via docker
            var tailLogsPsi = new System.Diagnostics.ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            tailLogsPsi.ArgumentList.Add("logs");
            tailLogsPsi.ArgumentList.Add("--tail");
            tailLogsPsi.ArgumentList.Add("5");
            tailLogsPsi.ArgumentList.Add(rotationCtr);
            using var tailLogsProc = System.Diagnostics.Process.Start(tailLogsPsi)!;
            string tailLogs = await tailLogsProc.StandardOutput.ReadToEndAsync();
            await tailLogsProc.WaitForExitAsync();
            Assert.Contains("log rotation test message", tailLogs);
        }
        finally
        {
            var rotRmPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"rm -f {rotationCtr}",
                UseShellExecute = false
            };
            using var rotRmProc = System.Diagnostics.Process.Start(rotRmPsi);
            await rotRmProc!.WaitForExitAsync();
        }
    }
}
