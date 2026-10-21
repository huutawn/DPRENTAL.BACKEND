using System.Text;
using Moq;
using Confluent.Kafka;
using Dprental.RecommendationService.Api.Tests.TestHelpers;
using Dprental.RecommendationService.Application.Kafka;
using Dprental.RecommendationService.Application.Kafka.Consumers;
using Dprental.RecommendationService.Application.Kafka.Options;
using Dprental.RecommendationService.Application.Kafka.Producer;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Application.Validation;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class KafkaRealBrokerIntegrationTests : IClassFixture<PostgreSqlTestcontainerFixture>
{
    private const string BootstrapServers = "127.0.0.1:29092";
    private readonly PostgreSqlTestcontainerFixture _postgresFixture;

    public KafkaRealBrokerIntegrationTests(PostgreSqlTestcontainerFixture postgresFixture)
    {
        _postgresFixture = postgresFixture;
    }

    private static async Task<bool> IsKafkaAvailableAsync()
    {
        try
        {
            var config = new AdminClientConfig { BootstrapServers = BootstrapServers, SocketTimeoutMs = 1500 };
            using var admin = new AdminClientBuilder(config).Build();
            var meta = admin.GetMetadata(TimeSpan.FromSeconds(2));
            return meta != null && meta.Brokers.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task RealKafka_DlqProducer_DeliversAuditedRecord_AndCanBeReadBack()
    {
        if (!await IsKafkaAvailableAsync())
        {
            return;
        }

        string dlqTopic = $"test.dlq.{Guid.NewGuid():N}";
        var options = Options.Create(new KafkaOptions { BootstrapServers = BootstrapServers });
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

        // Consume back with consumer to verify headers
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"verify-dlq-group-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string?, string>(consumerConfig).Build();
        consumer.Subscribe(dlqTopic);

        var record = consumer.Consume(TimeSpan.FromSeconds(5));
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
        if (!await IsKafkaAvailableAsync())
        {
            return;
        }

        var db = _postgresFixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();

            string testTopic = $"test.inventory.{Guid.NewGuid():N}";
            string testGroup = $"test.group.{Guid.NewGuid():N}";
            string testDlq = $"{testTopic}.dlq";

            var options = new KafkaOptions
            {
                BootstrapServers = BootstrapServers,
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

            var eventRepo = new InteractionEventRepository(db);
            var userInterestRepo = new UserEquipmentTypeInterestRepository(db);
            var listingPopularityRepo = new ListingPopularityRepository(db);
            var uow = new InteractionUnitOfWork(db);
            var validator = new EventSchemaValidator();

            var ingestionService = new InteractionIngestionService(
                validator,
                eventRepo,
                userInterestRepo,
                listingPopularityRepo,
                uow,
                Mock.Of<ISnapshotPublishingService>(),
                NullLogger<InteractionIngestionService>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton<IInteractionIngestionService>(ingestionService);
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            using var cts = new CancellationTokenSource();
            var worker = new RecommendationKafkaConsumerWorker(
                scopeFactory,
                dlqProducer,
                clientFactory,
                Options.Create(options),
                NullLogger<RecommendationKafkaConsumerWorker>.Instance);

            var workerTask = worker.StartAsync(cts.Token);

            var msgId = Guid.NewGuid();
            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();

            string viewJson = $$"""
            {
                "messageId": "{{msgId}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": null,
                    "equipmentListingId": "{{listingId}}",
                    "equipmentTypeId": "{{typeId}}"
                }
            }
            """;

            // 1. Publish once to Kafka
            using var producer = clientFactory.CreateProducer();
            await producer.ProduceAsync(testTopic, new Message<string?, string> { Key = listingId.ToString("D"), Value = viewJson });

            // Wait for worker to consume and persist
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                db.ChangeTracker.Clear();
                var pop = await db.ListingPopularities.FindAsync(listingId);
                if (pop != null && pop.Score == 2)
                {
                    break;
                }
                await Task.Delay(100);
            }

            db.ChangeTracker.Clear();
            var finalPop1 = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(finalPop1);
            Assert.Equal(2, finalPop1.Score);

            // 2. Publish SAME message again (duplicate delivery over Kafka)
            await producer.ProduceAsync(testTopic, new Message<string?, string> { Key = listingId.ToString("D"), Value = viewJson });

            // Allow worker to process duplicate
            await Task.Delay(1500);

            // Verify score is NOT incremented twice and only 1 raw row exists
            db.ChangeTracker.Clear();
            var finalPop2 = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(finalPop2);
            Assert.Equal(2, finalPop2.Score);

            int rawCount = await db.InteractionEvents.CountAsync(e => e.MessageId == msgId);
            Assert.Equal(1, rawCount);

            await cts.CancelAsync();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }
}
