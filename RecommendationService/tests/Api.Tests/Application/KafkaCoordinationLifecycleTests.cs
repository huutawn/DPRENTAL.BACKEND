using Confluent.Kafka;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Kafka;
using Dprental.RecommendationService.Application.Kafka.Consumers;
using Dprental.RecommendationService.Application.Kafka.Options;
using Dprental.RecommendationService.Application.Kafka.Producer;
using Dprental.RecommendationService.Application.Validation;
using Microsoft.Extensions.DependencyInjection;
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
}
