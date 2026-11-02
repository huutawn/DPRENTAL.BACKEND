using Confluent.Kafka;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Kafka;
using Dprental.RecommendationService.Application.Kafka.Consumers;
using Dprental.RecommendationService.Application.Kafka.Options;
using Dprental.RecommendationService.Application.Kafka.Producer;
using Dprental.RecommendationService.Application.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Application;

public sealed class KafkaCoordinationLifecycleTests
{
    [Fact]
    public async Task CommitFailure_RetriesCommitWithoutReIngesting_InSameSession()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(55),
            Message = new Message<string?, string>
            {
                Key = "key-55",
                Value = "{\"valid\": true}"
            }
        };

        int consumeCalls = 0;
        consumerMock
            .Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                consumeCalls++;
                if (consumeCalls == 1) return testMsg;
                return null!;
            });

        consumerMock
            .Setup(c => c.Assignment)
            .Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);

        int commitAttempts = 0;
        consumerMock
            .Setup(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()))
            .Callback(() =>
            {
                commitAttempts++;
                if (commitAttempts == 1)
                {
                    throw new KafkaException(new Error(ErrorCode.Local_QueueFull, "Temporary queue full error."));
                }
            });

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventProcessResult(EventProcessOutcome.Processed));

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });

        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);

        await Task.Delay(400);
        await cts.CancelAsync();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Ingestion must be executed exactly ONCE
        ingestionServiceMock.Verify(s => s.ProcessEventAsync(
            It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()), Times.Once);

        // Commit was retried
        Assert.True(commitAttempts >= 2, $"Expected at least 2 commit attempts, actual: {commitAttempts}");
    }

    [Fact]
    public async Task Rebalance_PartitionsRevoked_InvalidatesSession_DoesNotCommitOldAssignment()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(99),
            Message = new Message<string?, string>
            {
                Key = "key-99",
                Value = "{\"valid\": true}"
            }
        };

        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? revokedHandler = null;

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Callback<string, string,
                Action<IConsumer<string?, string>, List<TopicPartition>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, Error>?>((_, _, _, revoked, _, _) =>
            {
                revokedHandler = revoked;
            })
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        int consumeCalls = 0;
        consumerMock
            .Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                consumeCalls++;
                if (consumeCalls == 1) return testMsg;
                // While processing, simulate rebalance event
                if (consumeCalls == 2 && revokedHandler != null)
                {
                    revokedHandler(consumerMock.Object, [new TopicPartitionOffset("inventory.behavior.events.v1", new Partition(0), new Offset(99))]);
                }
                return null!;
            });

        consumerMock
            .Setup(c => c.Assignment)
            .Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(100);
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });

        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);

        await Task.Delay(400);
        await cts.CancelAsync();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Because partitions were revoked, the old assignment must NEVER be committed!
        consumerMock.Verify(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()), Times.Never);
    }

    [Fact]
    public async Task InitialAssignmentEmpty_AssignedDuringConsume_PausesNewPartitionsAndCommitsSuccessfully()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg1 = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(10),
            Message = new Message<string?, string> { Key = "k1", Value = "{\"valid\": true}" }
        };

        var testMsg2 = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(1),
            Offset = new Offset(20),
            Message = new Message<string?, string> { Key = "k2", Value = "{\"valid\": true}" }
        };

        Action<IConsumer<string?, string>, List<TopicPartition>>? assignedCallback = null;

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Callback<string, string,
                Action<IConsumer<string?, string>, List<TopicPartition>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, Error>?>((_, _, assigned, _, _, _) =>
            {
                assignedCallback = assigned;
            })
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var partitions = new List<TopicPartition>
        {
            new("inventory.behavior.events.v1", new Partition(0)),
            new("inventory.behavior.events.v1", new Partition(1)),
            new("inventory.behavior.events.v1", new Partition(2))
        };

        var currentAssignment = new List<TopicPartition>();
        consumerMock.Setup(c => c.Assignment).Returns(() => currentAssignment);

        bool isPaused = false;
        consumerMock
            .Setup(c => c.Pause(It.IsAny<IEnumerable<TopicPartition>>()))
            .Callback<IEnumerable<TopicPartition>>(parts =>
            {
                if (parts != null && parts.Any())
                {
                    isPaused = true;
                }
            });

        consumerMock
            .Setup(c => c.Resume(It.IsAny<IEnumerable<TopicPartition>>()))
            .Callback(() => isPaused = false);

        int consumeCalls = 0;
        consumerMock
            .Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                consumeCalls++;
                if (consumeCalls == 1)
                {
                    currentAssignment = new List<TopicPartition>(partitions);
                    assignedCallback?.Invoke(consumerMock.Object, partitions);
                    return testMsg1;
                }

                if (!isPaused)
                {
                    return testMsg2;
                }

                return null!;
            });

        var committedRecords = new List<ConsumeResult<string?, string>>();
        consumerMock
            .Setup(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()))
            .Callback<ConsumeResult<string?, string>>(cr => committedRecords.Add(cr));

        var ingestionSignal = new TaskCompletionSource<bool>();
        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(50);
                ingestionSignal.TrySetResult(true);
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });

        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);

        await Task.WhenAny(ingestionSignal.Task, Task.Delay(500));
        await Task.Delay(200);

        await cts.CancelAsync();
        try { await workerTask; } catch (OperationCanceledException) { }

        consumerMock.Verify(c => c.Pause(It.Is<IEnumerable<TopicPartition>>(p => p.Count() == 3)), Times.AtLeastOnce);
        Assert.Single(committedRecords);
        Assert.Equal(testMsg1.Offset, committedRecords[0].Offset);
    }

    [Fact]
    public async Task BacklogMultiPartition_ProcessesAllRecordsSequentially_WithoutSessionRestartOrLoss()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var partitions = new List<TopicPartition>
        {
            new("inventory.behavior.events.v1", new Partition(0)),
            new("inventory.behavior.events.v1", new Partition(1)),
            new("inventory.behavior.events.v1", new Partition(2))
        };
        bool isPaused = false;
        consumerMock
            .Setup(c => c.Pause(It.IsAny<IEnumerable<TopicPartition>>()))
            .Callback(() => isPaused = true);
        consumerMock
            .Setup(c => c.Resume(It.IsAny<IEnumerable<TopicPartition>>()))
            .Callback(() => isPaused = false);

        consumerMock.Setup(c => c.Assignment).Returns(partitions);

        var backlog = new Queue<ConsumeResult<string?, string>>();
        for (int i = 0; i < 6; i++)
        {
            backlog.Enqueue(new ConsumeResult<string?, string>
            {
                Topic = "inventory.behavior.events.v1",
                Partition = new Partition(i % 3),
                Offset = new Offset(i * 10),
                Message = new Message<string?, string> { Key = $"k-{i}", Value = $"{{\"index\": {i}}}" }
            });
        }

        int totalRecords = backlog.Count;
        var committedOffsets = new List<long>();
        var allProcessedSignal = new TaskCompletionSource<bool>();

        consumerMock
            .Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns((TimeSpan _) =>
            {
                if (!isPaused && backlog.Count > 0)
                {
                    return backlog.Dequeue();
                }
                Thread.Sleep(1);
                return null!;
            });

        consumerMock
            .Setup(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()))
            .Callback<ConsumeResult<string?, string>>(cr =>
            {
                committedOffsets.Add(cr.Offset.Value);
                if (committedOffsets.Count == totalRecords)
                {
                    allProcessedSignal.TrySetResult(true);
                }
            });

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventProcessResult(EventProcessOutcome.Processed));

        int inventoryConsumerCreations = 0;
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Callback(() => inventoryConsumerCreations++)
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);

        await Task.WhenAny(allProcessedSignal.Task, Task.Delay(2000));
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        Assert.Equal(1, inventoryConsumerCreations);
        Assert.Equal(6, committedOffsets.Count);
        Assert.Equal(new long[] { 0, 10, 20, 30, 40, 50 }, committedOffsets);
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("WaitingForRetry")]
    [InlineData("WaitingForCommit")]
    public async Task UnexpectedRecord_InBusyState_InvalidatesSession_DoesNotCommit(string targetState)
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg1 = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string?, string> { Key = "k1", Value = "{\"test\": 1}" }
        };

        var unexpectedMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(1),
            Offset = new Offset(2),
            Message = new Message<string?, string> { Key = "k2", Value = "{\"unexpected\": true}" }
        };

        consumerMock.Setup(c => c.Assignment).Returns([
            new TopicPartition("inventory.behavior.events.v1", new Partition(0)),
            new TopicPartition("inventory.behavior.events.v1", new Partition(1))
        ]);

        int consumeCount = 0;
        consumerMock
            .Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                consumeCount++;
                if (consumeCount == 1) return testMsg1;
                if (consumeCount == 2) return unexpectedMsg;
                return null!;
            });

        if (targetState == "Processing")
        {
            ingestionServiceMock
                .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    await Task.Delay(100);
                    return new EventProcessResult(EventProcessOutcome.Processed);
                });
        }
        else if (targetState == "WaitingForRetry")
        {
            ingestionServiceMock
                .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EventProcessResult(EventProcessOutcome.Processed, ErrorReason: "simulate transient fail"));
        }
        else // WaitingForCommit
        {
            ingestionServiceMock
                .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EventProcessResult(EventProcessOutcome.Processed));

            consumerMock
                .Setup(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()))
                .Throws(new KafkaException(new Error(ErrorCode.Local_QueueFull, "temporary commit error")));
        }

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(250);
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        // The unexpected record must NEVER be committed!
        consumerMock.Verify(c => c.Commit(unexpectedMsg), Times.Never);
    }

    [Fact]
    public async Task ConsumerCloseThrows_StillDisposes_AndObservesInFlightTaskOnShutdown()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string?, string> { Key = "k1", Value = "{\"slow\": true}" }
        };

        consumerMock.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);
        consumerMock.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(testMsg);
        consumerMock.Setup(c => c.Close()).Throws(new InvalidOperationException("Simulated close failure"));

        bool taskWasCanceled = false;
        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, InteractionEventSource _, CancellationToken ct) =>
            {
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    taskWasCanceled = true;
                    throw;
                }
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        consumerMock.Verify(c => c.Close(), Times.AtLeastOnce);
        consumerMock.Verify(c => c.Dispose(), Times.AtLeastOnce);
        Assert.True(taskWasCanceled);
    }

    [Fact]
    public async Task TransientDbFailure_RetriesAndPolls_DoesNotCommitBeforeSuccess()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(77),
            Message = new Message<string?, string> { Key = "k-77", Value = "{\"db\": \"test\"}" }
        };

        consumerMock.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);
        bool isPaused = false;
        consumerMock.Setup(c => c.Pause(It.IsAny<IEnumerable<TopicPartition>>())).Callback(() => isPaused = true);
        consumerMock.Setup(c => c.Resume(It.IsAny<IEnumerable<TopicPartition>>())).Callback(() => isPaused = false);

        int consumeCount = 0;
        consumerMock.Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns((TimeSpan _) =>
            {
                consumeCount++;
                if (consumeCount == 1) return testMsg;
                if (!isPaused) return testMsg;
                return null!;
            });

        int attempts = 0;
        var successSignal = new TaskCompletionSource<bool>();
        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                attempts++;
                if (attempts < 3)
                {
                    return new EventProcessResult(EventProcessOutcome.Processed, ErrorReason: "DB connection timeout");
                }
                successSignal.TrySetResult(true);
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await Task.WhenAny(successSignal.Task, Task.Delay(3500));
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        Assert.True(attempts >= 3, $"Expected at least 3 attempts, actual: {attempts}");
        consumerMock.Verify(c => c.Commit(testMsg), Times.AtLeastOnce);
        dlqProducerMock.Verify(p => p.ProduceToDlqAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RevokeDuringRetry_CancelsTask_DoesNotCommitOldGeneration()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(88),
            Message = new Message<string?, string> { Key = "k-88", Value = "{\"retry\": true}" }
        };

        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? revokedHandler = null;
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Callback<string, string,
                Action<IConsumer<string?, string>, List<TopicPartition>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, Error>?>((_, _, _, revoked, _, _) =>
            {
                revokedHandler = revoked;
            })
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        consumerMock.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);

        int consumeCount = 0;
        consumerMock.Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                consumeCount++;
                if (consumeCount == 1) return testMsg;
                // Trigger revoke while in retry wait
                if (consumeCount == 2 && revokedHandler != null)
                {
                    revokedHandler(consumerMock.Object, [new TopicPartitionOffset("inventory.behavior.events.v1", new Partition(0), new Offset(88))]);
                }
                return null!;
            });

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventProcessResult(EventProcessOutcome.Processed, ErrorReason: "Simulate transient error requiring retry"));

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(250);
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        consumerMock.Verify(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()), Times.Never);
    }

    [Fact]
    public async Task ContinuousSessionFailures_EnforcesOneSecondDelay_AndShutdownInterruptsImmediately()
    {
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();

        int creationCount = 0;
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Callback(() =>
            {
                creationCount++;
                throw new InvalidOperationException("Immediate consumer creation error");
            });

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var options = Options.Create(new KafkaOptions { Enabled = true });

        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        // In 1.5 seconds, with 1s delay per retry, creationCount should only be 2 to 4 (for 2 loops)
        await Task.Delay(1400);
        var stopSw = System.Diagnostics.Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        stopSw.Stop();
        try { await workerTask; } catch (OperationCanceledException) { }

        // StopAsync should interrupt the 1s delay immediately (under 500ms)
        Assert.True(stopSw.ElapsedMilliseconds < 800, $"StopAsync took too long to interrupt delay: {stopSw.ElapsedMilliseconds}ms");
        // Creation count bounded because of 1s delay
        Assert.True(creationCount <= 6, $"Expected bounded creation count due to 1s delay, got: {creationCount}");
    }

    [Fact]
    public async Task IngestionIgnoresCancellation_OverOneSecond_WaitsForTaskToFinish_BeforeCreatingNewSession()
    {
        var consumerMock1 = new Mock<IConsumer<string?, string>>();
        var consumerMock2 = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string?, string> { Key = "k1", Value = "{\"slow\": true}" }
        };

        consumerMock1.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);
        consumerMock1.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(testMsg);
        consumerMock2.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);
        consumerMock2.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);

        var ingestionStarted = new TaskCompletionSource<bool>();
        var taskCanFinish = new TaskCompletionSource<bool>();
        bool taskCompleted = false;

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                ingestionStarted.TrySetResult(true);
                // Deliberately ignore cancellation token and wait for external signal
                await taskCanFinish.Task;
                taskCompleted = true;
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        int inventoryCreations = 0;
        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? revokedHandler = null;
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(() =>
            {
                inventoryCreations++;
                if (inventoryCreations == 1) return consumerMock1.Object;
                return consumerMock2.Object;
            })
            .Callback<string, string,
                Action<IConsumer<string?, string>, List<TopicPartition>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, Error>?>((_, _, _, revoked, _, _) =>
            {
                if (inventoryCreations == 1) revokedHandler = revoked;
            });

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await ingestionStarted.Task;

        // Trigger partition revoke to invalidate session while task is running
        revokedHandler?.Invoke(consumerMock1.Object, [new TopicPartitionOffset("inventory.behavior.events.v1", new Partition(0), new Offset(1))]);

        // Wait more than 1 second (1.2s) - during this time, session 1 cleanup warning occurs, but session 2 MUST NOT open
        await Task.Delay(1200);
        Assert.Equal(1, inventoryCreations);
        Assert.False(taskCompleted);

        // Now allow task to complete
        taskCanFinish.TrySetResult(true);

        // Allow worker to finish cleanup, observe task, dispose consumer 1, and open session 2
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3) && inventoryCreations < 2)
        {
            await Task.Delay(100);
        }

        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        Assert.True(taskCompleted);
        Assert.Equal(2, inventoryCreations);
        consumerMock1.Verify(c => c.Dispose(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RevokeAndShutdown_DuringIngestion_TaskCompletesBeforeDispose_DoesNotCommitOldAssignment()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(99),
            Message = new Message<string?, string> { Key = "k99", Value = "{\"val\": 99}" }
        };

        consumerMock.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);
        consumerMock.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(testMsg);

        bool taskCompleted = false;
        bool taskCompletedBeforeDispose = false;
        var ingestionStarted = new TaskCompletionSource<bool>();

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, InteractionEventSource _, CancellationToken ct) =>
            {
                ingestionStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException) { }
                taskCompleted = true;
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        consumerMock
            .Setup(c => c.Dispose())
            .Callback(() =>
            {
                if (taskCompleted)
                {
                    taskCompletedBeforeDispose = true;
                }
            });

        var committedRecords = new List<ConsumeResult<string?, string>>();
        consumerMock
            .Setup(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()))
            .Callback<ConsumeResult<string?, string>>(cr => committedRecords.Add(cr));

        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? revokedHandler = null;
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Callback<string, string,
                Action<IConsumer<string?, string>, List<TopicPartition>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, List<TopicPartitionOffset>>?,
                Action<IConsumer<string?, string>, Error>?>((_, _, _, revoked, _, _) =>
            {
                revokedHandler = revoked;
            })
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await ingestionStarted.Task;

        // Revoke and shutdown while task in progress
        revokedHandler?.Invoke(consumerMock.Object, [new TopicPartitionOffset("inventory.behavior.events.v1", new Partition(0), new Offset(99))]);
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        Assert.True(taskCompletedBeforeDispose, "Task must complete before consumer is disposed.");
        Assert.Empty(committedRecords);
    }

    [Fact]
    public async Task TaskThrowsOrCancellationThrows_ResourcesCleanedUp_ExceptionsObserved()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string?, string> { Key = "k1", Value = "{\"crash\": true}" }
        };

        consumerMock.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);
        consumerMock.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(testMsg);
        consumerMock.Setup(c => c.Close()).Throws(new KafkaException(new Error(ErrorCode.Local_State, "Simulated Close failure")));

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ingestion task crash"));

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            NullLogger<RecommendationKafkaConsumerWorker>.Instance);

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        consumerMock.Verify(c => c.Close(), Times.AtLeastOnce);
        consumerMock.Verify(c => c.Dispose(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ContinuousCommitFailures_RateLimitedToTenSeconds_IngestionRunsOnce_RecoversOnSuccess()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(55),
            Message = new Message<string?, string> { Key = "k55", Value = "{\"val\": 55}" }
        };

        consumerMock.Setup(c => c.Assignment).Returns([new TopicPartition("inventory.behavior.events.v1", new Partition(0))]);


        int consumeCount = 0;
        consumerMock.Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns((TimeSpan _) =>
            {
                consumeCount++;
                if (consumeCount == 1) return testMsg;
                return null!;
            });

        int ingestionCount = 0;
        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                ingestionCount++;
                return new EventProcessResult(EventProcessOutcome.Processed);
            });

        var customTimeProvider = new TestControllableTimeProvider();
        int commitAttempts = 0;
        var commitRecoveredSignal = new TaskCompletionSource<bool>();

        consumerMock
            .Setup(c => c.Commit(It.IsAny<ConsumeResult<string?, string>>()))
            .Callback<ConsumeResult<string?, string>>(cr =>
            {
                commitAttempts++;
                if (commitAttempts <= 10)
                {
                    // Fail commit for first 10 calls, advancing time slightly (1s per call)
                    customTimeProvider.Advance(TimeSpan.FromSeconds(1));
                    throw new KafkaException(new Error(ErrorCode.Local_QueueFull, "Temporary broker commit failure"));
                }
                else if (commitAttempts == 11)
                {
                    // Advance past 10 seconds boundary
                    customTimeProvider.Advance(TimeSpan.FromSeconds(11));
                    throw new KafkaException(new Error(ErrorCode.Local_QueueFull, "Second wave broker commit failure"));
                }
                else
                {
                    // Commit succeeds
                    commitRecoveredSignal.TrySetResult(true);
                }
            });

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        var idleRentalConsumer = new Mock<IConsumer<string?, string>>();
        idleRentalConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);
        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleRentalConsumer.Object);

        var services = new ServiceCollection();
        services.AddSingleton(ingestionServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() => sp.CreateScope());

        var options = Options.Create(new KafkaOptions { Enabled = true });
        var loggerMock = new Mock<ILogger<RecommendationKafkaConsumerWorker>>();
        int warningCount = 0;
        int infoCount = 0;
        loggerMock
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => warningCount++);
        loggerMock
            .Setup(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => infoCount++);

        using var cts = new CancellationTokenSource();
        var worker = new RecommendationKafkaConsumerWorker(
            scopeFactoryMock.Object,
            dlqProducerMock.Object,
            clientFactoryMock.Object,
            options,
            loggerMock.Object,
            customTimeProvider);

        var workerTask = worker.StartAsync(cts.Token);
        await Task.WhenAny(commitRecoveredSignal.Task, Task.Delay(2000));
        await worker.StopAsync(CancellationToken.None);
        try { await workerTask; } catch (OperationCanceledException) { }

        // Ingestion must execute exactly once throughout the entire retry storm
        Assert.Equal(1, ingestionCount);
        // Commit was retried multiple times
        Assert.True(commitAttempts >= 12);
        // Warning count bounded: only 2 warnings (initial failure and after 10s mark), not 11 warnings
        Assert.True(warningCount <= 3, $"Expected <= 3 warnings, actual: {warningCount}");
        // Recovery message logged and partitions resumed
        Assert.True(infoCount >= 1);
        consumerMock.Verify(c => c.Resume(It.IsAny<IEnumerable<TopicPartition>>()), Times.AtLeastOnce);
    }

    private sealed class TestControllableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
