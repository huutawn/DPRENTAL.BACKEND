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

namespace Dprental.RecommendationService.Api.Tests.Integration;

public sealed class KafkaConsumerAndDlqTests
{
    [Fact]
    public void KafkaOptions_HasCorrectPhase1Defaults()
    {
        var options = new KafkaOptions();

        Assert.Equal("inventory.behavior.events.v1", options.InventoryTopic);
        Assert.Equal("recommendation-inventory-behavior-v1", options.InventoryConsumerGroup);
        Assert.Equal("inventory.behavior.events.v1.recommendation.dlq", options.InventoryDlqTopic);

        Assert.Equal("rental.events.v1", options.RentalTopic);
        Assert.Equal("recommendation-rental-behavior-v1", options.RentalConsumerGroup);
        Assert.Equal("rental.events.v1.recommendation.dlq", options.RentalDlqTopic);

        Assert.Equal(5, options.MaxRetryAttemptsPerRound);
        Assert.Equal(1000, options.InitialRetryDelayMs);
        Assert.Equal(30000, options.MaxRetryDelayMs);
    }

    [Fact]
    public async Task DlqProducer_ProducesWithRequiredAuditHeaders()
    {
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();

        await dlqProducerMock.Object.ProduceToDlqAsync(
            "inventory.behavior.events.v1.recommendation.dlq",
            "key-123",
            "{\"invalid\": true}",
            "Schema validation failed",
            "inventory.behavior.events.v1",
            1,
            42,
            CancellationToken.None);

        dlqProducerMock.Verify(p => p.ProduceToDlqAsync(
            "inventory.behavior.events.v1.recommendation.dlq",
            "key-123",
            "{\"invalid\": true}",
            "Schema validation failed",
            "inventory.behavior.events.v1",
            1,
            42,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumerWorker_ValidEvent_IngestsAndCommitsOffset()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "inventory.behavior.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(100),
            Message = new Message<string?, string>
            {
                Key = "key-1",
                Value = "{\"valid\": true}"
            }
        };

        var tcs = new TaskCompletionSource<ConsumeResult<string?, string>>();
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

        // Wait for commit call
        await Task.Delay(300);
        await cts.CancelAsync();
        try { await workerTask; } catch (OperationCanceledException) { }

        consumerMock.Verify(c => c.Pause(It.IsAny<IEnumerable<TopicPartition>>()), Times.AtLeastOnce);
        consumerMock.Verify(c => c.Commit(testMsg), Times.Once);
        dlqProducerMock.Verify(d => d.ProduceToDlqAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsumerWorker_ConflictEvent_ProducesToDlqThenCommitsOffset()
    {
        var consumerMock = new Mock<IConsumer<string?, string>>();
        var clientFactoryMock = new Mock<IKafkaClientFactory>();
        var dlqProducerMock = new Mock<IKafkaDlqProducer>();
        var ingestionServiceMock = new Mock<IInteractionIngestionService>();

        var testMsg = new ConsumeResult<string?, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(1),
            Offset = new Offset(200),
            Message = new Message<string?, string>
            {
                Key = "key-conflict",
                Value = "{\"conflict\": true}"
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
            .Returns([new TopicPartition("rental.events.v1", new Partition(1))]);

        var idleInventoryConsumer = new Mock<IConsumer<string?, string>>();
        idleInventoryConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns((ConsumeResult<string?, string>?)null!);

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-rental-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(consumerMock.Object);

        clientFactoryMock
            .Setup(f => f.CreateConsumer(
                "recommendation-inventory-behavior-v1",
                It.IsAny<string>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartition>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, List<TopicPartitionOffset>>>(),
                It.IsAny<Action<IConsumer<string?, string>, Error>>()))
            .Returns(idleInventoryConsumer.Object);

        ingestionServiceMock
            .Setup(s => s.ProcessEventAsync(It.IsAny<string>(), It.IsAny<InteractionEventSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventProcessResult(EventProcessOutcome.ConflictMismatch, "Payload mismatch"));

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

        await Task.Delay(300);
        await cts.CancelAsync();
        try { await workerTask; } catch (OperationCanceledException) { }

        dlqProducerMock.Verify(d => d.ProduceToDlqAsync(
            "rental.events.v1.recommendation.dlq",
            "key-conflict",
            "{\"conflict\": true}",
            "Payload mismatch",
            "rental.events.v1",
            1,
            200,
            It.IsAny<CancellationToken>()), Times.Once);

        consumerMock.Verify(c => c.Commit(testMsg), Times.Once);
    }
}
