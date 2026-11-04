using Confluent.Kafka;
using Dprental.NotificationService.Application.Kafka.Consumers;
using Dprental.NotificationService.Application.Kafka.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Kafka;

public sealed class KafkaConsumerHostedServiceTests
{
    [Fact]
    public void ResolveConfiguredTopics_CommaSeparatedString_ResolvesAllTrimmedTopics()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:Topics"] = "rental.events.v1, payment.analytics.v1, custom.topic.v1"
            })
            .Build();

        var topics = KafkaConsumerHostedService.ResolveConfiguredTopics(config);

        Assert.Equal(3, topics.Count);
        Assert.Equal("rental.events.v1", topics[0]);
        Assert.Equal("payment.analytics.v1", topics[1]);
        Assert.Equal("custom.topic.v1", topics[2]);
    }

    [Fact]
    public void ResolveConfiguredTopics_ConfigurationList_ResolvesAllItems()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:Topics:0"] = "rental.events.v1",
                ["Kafka:Topics:1"] = "payment.analytics.v1"
            })
            .Build();

        var topics = KafkaConsumerHostedService.ResolveConfiguredTopics(config);

        Assert.Equal(2, topics.Count);
        Assert.Equal("rental.events.v1", topics[0]);
        Assert.Equal("payment.analytics.v1", topics[1]);
    }

    [Fact]
    public void ResolveConfiguredTopics_EmptyOrMissing_DefaultsToRentalAndPaymentTopics()
    {
        var config = new ConfigurationBuilder().Build();

        var topics = KafkaConsumerHostedService.ResolveConfiguredTopics(config);

        Assert.Equal(2, topics.Count);
        Assert.Equal("rental.events.v1", topics[0]);
        Assert.Equal("payment.analytics.v1", topics[1]);
    }

    [Fact]
    public async Task Consumer_SubscribesAllConfiguredTopicsAndPassesGroupId()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:Topics"] = "rental.events.v1,payment.analytics.v1",
                ["Kafka:GroupId"] = "custom-notification-group"
            })
            .Build();

        ConsumerConfig? capturedConfig = null;
        IEnumerable<string>? subscribedTopics = null;

        using var cts = new CancellationTokenSource();
        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>()))
            .Callback<IEnumerable<string>>(topics =>
            {
                subscribedTopics = topics.ToList();
                cts.Cancel();
            });
        var service = new KafkaConsumerHostedService(
            new ServiceCollection().BuildServiceProvider(),
            config,
            NullLogger<KafkaConsumerHostedService>.Instance,
            hostApplicationLifetime: null,
            consumerFactory: cfg =>
            {
                capturedConfig = cfg;
                return mockConsumer.Object;
            });

        await service.ExecuteConsumerAsync(cts.Token);

        Assert.NotNull(capturedConfig);
        Assert.Equal("custom-notification-group", capturedConfig.GroupId);
        Assert.NotNull(subscribedTopics);
        var topicList = subscribedTopics.ToList();
        Assert.Equal(2, topicList.Count);
        Assert.Contains("rental.events.v1", topicList);
        Assert.Contains("payment.analytics.v1", topicList);
    }

    [Fact]
    public async Task Consumer_SuccessfulProcessing_CommitsOffset()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:Topics"] = "rental.events.v1,payment.analytics.v1"
            })
            .Build();

        var consumeResult = new ConsumeResult<string, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(100),
            Message = new Message<string, string> { Key = "k1", Value = "{}" }
        };

        var committed = false;
        var mockConsumer = new Mock<IConsumer<string, string>>();
        using var cts = new CancellationTokenSource();

        var hasConsumed = false;
        mockConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                if (!hasConsumed)
                {
                    hasConsumed = true;
                    return consumeResult;
                }
                cts.Cancel();
                return null!;
            });

        mockConsumer.Setup(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()))
            .Callback<ConsumeResult<string, string>>(_ => committed = true);

        var mockIngestion = new Mock<INotificationIngestionService>();
        mockIngestion.Setup(i => i.IngestMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IngestionOutcome.Processed);

        var services = new ServiceCollection();
        services.AddScoped(_ => mockIngestion.Object);
        var sp = services.BuildServiceProvider();

        var service = new KafkaConsumerHostedService(
            sp,
            config,
            NullLogger<KafkaConsumerHostedService>.Instance,
            hostApplicationLifetime: null,
            consumerFactory: _ => mockConsumer.Object);

        await service.ExecuteConsumerAsync(cts.Token);

        Assert.True(committed);
    }

    [Theory]
    [InlineData(IngestionOutcome.Ignored)]
    [InlineData(IngestionOutcome.Duplicate)]
    [InlineData(IngestionOutcome.Quarantined)]
    public async Task Consumer_ValidIgnoreOrDuplicateOrDlqAck_CommitsOffset(IngestionOutcome outcome)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:Topics"] = "rental.events.v1,payment.analytics.v1"
            })
            .Build();

        var consumeResult = new ConsumeResult<string, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(100),
            Message = new Message<string, string> { Key = "k1", Value = "{}" }
        };

        var committed = false;
        var mockConsumer = new Mock<IConsumer<string, string>>();
        using var cts = new CancellationTokenSource();

        var hasConsumed = false;
        mockConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(() =>
            {
                if (!hasConsumed)
                {
                    hasConsumed = true;
                    return consumeResult;
                }
                cts.Cancel();
                return null!;
            });

        mockConsumer.Setup(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()))
            .Callback<ConsumeResult<string, string>>(_ => committed = true);

        var mockIngestion = new Mock<INotificationIngestionService>();
        mockIngestion.Setup(i => i.IngestMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var services = new ServiceCollection();
        services.AddScoped(_ => mockIngestion.Object);
        var sp = services.BuildServiceProvider();

        var service = new KafkaConsumerHostedService(
            sp,
            config,
            NullLogger<KafkaConsumerHostedService>.Instance,
            hostApplicationLifetime: null,
            consumerFactory: _ => mockConsumer.Object);

        await service.ExecuteConsumerAsync(cts.Token);

        Assert.True(committed);
    }

    [Fact]
    public async Task Consumer_InfrastructureFailureAfter3Retries_StopsHostWithoutCommittingOffset()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:Topics"] = "rental.events.v1,payment.analytics.v1"
            })
            .Build();

        var consumeResult = new ConsumeResult<string, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(100),
            Message = new Message<string, string> { Key = "k1", Value = "{}" }
        };

        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>()))
            .Returns(consumeResult);

        var commitCalled = false;
        mockConsumer.Setup(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()))
            .Callback<ConsumeResult<string, string>>(_ => commitCalled = true);

        var mockLifetime = new Mock<IHostApplicationLifetime>();
        var stopRequested = false;
        mockLifetime.Setup(l => l.StopApplication())
            .Callback(() => stopRequested = true);

        var attempts = 0;
        var scopesCreated = 0;

        var services = new ServiceCollection();
        services.AddScoped<INotificationIngestionService>(_ =>
        {
            scopesCreated++;
            var mock = new Mock<INotificationIngestionService>();
            mock.Setup(i => i.IngestMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => attempts++)
                .ThrowsAsync(new InvalidOperationException("Simulated database/broker connection failure"));
            return mock.Object;
        });
        var sp = services.BuildServiceProvider();

        var service = new KafkaConsumerHostedService(
            sp,
            config,
            NullLogger<KafkaConsumerHostedService>.Instance,
            hostApplicationLifetime: mockLifetime.Object,
            consumerFactory: _ => mockConsumer.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.ExecuteConsumerAsync(cts.Token);

        Assert.Equal(3, attempts);
        Assert.Equal(3, scopesCreated);
        Assert.False(commitCalled, "Offset MUST NOT be committed on infrastructure failure");
        Assert.True(stopRequested, "IHostApplicationLifetime.StopApplication() MUST be called after 3 retries");
    }
}
