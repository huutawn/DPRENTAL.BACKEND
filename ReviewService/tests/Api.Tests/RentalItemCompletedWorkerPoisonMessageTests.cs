#pragma warning disable CA1822
using System.Text.Json;
using Confluent.Kafka;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.Kafka.Consumers;
using Dprental.ReviewService.Application.Kafka.Contracts;
using Dprental.ReviewService.Application.Kafka.Producers;
using Dprental.ReviewService.Application.Kafka.Workers;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class RentalItemCompletedWorkerPoisonMessageTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static ReviewServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ReviewServiceDbContext(options);
    }

    [Theory]
    [InlineData("[]", "INVALID_ROOT_TYPE")]
    [InlineData("null", "INVALID_ROOT_TYPE")]
    [InlineData("123", "INVALID_ROOT_TYPE")]
    [InlineData("{}", "INVALID_MESSAGE_TYPE")]
    [InlineData("{\"messageType\":123}", "INVALID_MESSAGE_TYPE")]
    [InlineData("{\"messageType\":\"   \"}", "EMPTY_MESSAGE_TYPE")]
    [InlineData("{\"messageType\":\"RentalItemCompleted\",\"messageId\":123}", "INVALID_MESSAGE_ID")]
    [InlineData("{\"messageType\":\"RentalItemCompleted\",\"messageId\":\"990e8400-e29b-41d4-a716-446655440004\",\"version\":\"1\"}", "INVALID_VERSION")]
    [InlineData("{\"messageType\":\"RentalItemCompleted\",\"messageId\":\"990e8400-e29b-41d4-a716-446655440004\",\"version\":1,\"producer\":123}", "INVALID_PRODUCER")]
    [InlineData("{\"messageType\":\"RentalItemCompleted\",\"messageId\":\"990e8400-e29b-41d4-a716-446655440004\",\"version\":1,\"producer\":\"RentalService\",\"payload\":[]}", "INVALID_PAYLOAD_OBJECT")]
    public async Task Consumer_PoisonPayloads_CheckedSafely_RoutedToDlqAndDeadLettered(string poisonJson, string expectedErrorCode)
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString("N"));
        var inboxRepo = new InboxRepository(db);
        var projRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var eligibilityService = new ReviewEligibilityService(projRepo, timeProvider);
        var spyProducer = new SpyDlqProducer(returnSuccess: true);
        var mockClient = new MockRentalClient();

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, mockClient, eligibilityService, spyProducer, timeProvider);

        var context = new KafkaMessageContext("rental.events.v1", 0, 10, "poison-key", poisonJson);

        var outcome = await consumer.ProcessMessageAsync(context);

        Assert.Equal(ProcessOutcome.DeadLettered, outcome);
        Assert.Single(spyProducer.Messages);
        Assert.Equal(RentalItemCompletedConsumer.DlqTopic, spyProducer.Messages[0].Topic);
        Assert.Equal("rental.events.v1:0:10", spyProducer.Messages[0].Key);

        using var doc = JsonDocument.Parse(spyProducer.Messages[0].Payload);
        Assert.Equal(expectedErrorCode, doc.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Consumer_ValidUnrelatedEvent_ReturnedAsIgnoredWithoutPublishingToDlq()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString("N"));
        var inboxRepo = new InboxRepository(db);
        var projRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var eligibilityService = new ReviewEligibilityService(projRepo, timeProvider);
        var spyProducer = new SpyDlqProducer(returnSuccess: true);
        var mockClient = new MockRentalClient();

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, mockClient, eligibilityService, spyProducer, timeProvider);

        var unrelatedJson = JsonSerializer.Serialize(new
        {
            messageId = Guid.NewGuid(),
            messageType = "RentalOrderPaid",
            version = 1,
            producer = "RentalService",
            aggregateId = "order-123",
            occurredAt = FixedNow
        });

        var context = new KafkaMessageContext("rental.events.v1", 0, 20, "unrelated-key", unrelatedJson);

        var outcome = await consumer.ProcessMessageAsync(context);

        Assert.Equal(ProcessOutcome.Ignored, outcome);
        Assert.Empty(spyProducer.Messages); // Must NOT be published to DLQ
    }

    [Fact]
    public async Task Worker_ProcessesValidEvent_ImmediatelyAfterPoisonMessage()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<ReviewServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddReviewServiceRepositories();
        services.AddScoped<IReviewEligibilityService, ReviewEligibilityService>();
        services.AddScoped<IRentalItemCompletedConsumer, RentalItemCompletedConsumer>();

        var spyProducer = new SpyDlqProducer(returnSuccess: true);
        services.AddSingleton<IKafkaProducer>(spyProducer);

        var validOrderId = Guid.NewGuid();
        var validItemId = Guid.NewGuid();
        var validListingId = Guid.NewGuid();
        var validRenterId = Guid.NewGuid();
        var validSupplierId = Guid.NewGuid();
        var completedAt = FixedNow.AddDays(-2);

        var snapshot = new RentalItemReviewSnapshotDto(
            validOrderId, validItemId, validRenterId, validSupplierId, validListingId, "Completed", completedAt, 1);
        var mockClient = new MockRentalClient(snapshot);
        services.AddSingleton<IRentalServiceClient>(mockClient);

        var timeProvider = new TestTimeProvider(FixedNow);
        services.AddSingleton<TimeProvider>(timeProvider);

        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = "127.0.0.1:9092"
            })
            .Build();

        // 1. Poison message (root is an array `[]`)
        var poisonRecord = new ConsumeResult<string, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = "k-0", Value = "[]" }
        };

        // 2. Subsequent valid RentalItemCompleted event
        var validEnvelope = new
        {
            messageId = Guid.NewGuid(),
            messageType = "RentalItemCompleted",
            version = 1,
            producer = "RentalService",
            aggregateId = validOrderId.ToString("D"),
            occurredAt = FixedNow,
            payload = new
            {
                rentalOrderId = validOrderId,
                rentalItemId = validItemId,
                userId = validRenterId,
                equipmentListingId = validListingId,
                orderVersion = 1,
                completedAt = completedAt
            }
        };

        var validRecord = new ConsumeResult<string, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string, string> { Key = "k-1", Value = JsonSerializer.Serialize(validEnvelope) }
        };

        var fakeConsumer = new FakeSequenceConsumer([poisonRecord, validRecord]);

        var worker = new RentalItemCompletedKafkaWorker(
            sp, config, NullLogger<RentalItemCompletedKafkaWorker>.Instance, _ => fakeConsumer);

        using var cts = new CancellationTokenSource();

        fakeConsumer.OnCommitCalled = () =>
        {
            if (fakeConsumer.CommittedOffsets.Count >= 2)
            {
                cts.Cancel();
            }
        };

        await worker.ExecuteWorkerAsync(cts.Token);
        // Assert 1: Poison message was dead-lettered and offset 0 was committed
        Assert.Single(spyProducer.Messages);
        Assert.Equal(RentalItemCompletedConsumer.DlqTopic, spyProducer.Messages[0].Topic);
        Assert.Contains(0, fakeConsumer.CommittedOffsets);

        // Assert 2: Valid event was processed and offset 1 was committed
        Assert.Contains(1, fakeConsumer.CommittedOffsets);

        // Assert 3: Valid item projection is recorded in database
        using var checkScope = sp.CreateScope();
        var projRepo = checkScope.ServiceProvider.GetRequiredService<IReviewProjectionRepository>();
        var proj = await projRepo.GetByItemIdAsync(validItemId);
        Assert.NotNull(proj);
        Assert.True(proj.IsEligible);
        Assert.Equal(completedAt, proj.CompletedAt);
    }

    [Fact]
    public async Task Worker_WhenDlqPublishFails_SourceOffsetIsNotCommitted()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<ReviewServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddReviewServiceRepositories();
        services.AddScoped<IReviewEligibilityService, ReviewEligibilityService>();
        services.AddScoped<IRentalItemCompletedConsumer, RentalItemCompletedConsumer>();

        // DLQ producer fails (broker outage)
        var spyProducer = new SpyDlqProducer(returnSuccess: false);
        services.AddSingleton<IKafkaProducer>(spyProducer);

        var mockClient = new MockRentalClient();
        services.AddSingleton<IRentalServiceClient>(mockClient);

        var timeProvider = new TestTimeProvider(FixedNow);
        services.AddSingleton<TimeProvider>(timeProvider);

        var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = "127.0.0.1:9092"
            })
            .Build();

        var poisonRecord = new ConsumeResult<string, string>
        {
            Topic = "rental.events.v1",
            Partition = new Partition(0),
            Offset = new Offset(55),
            Message = new Message<string, string> { Key = "k-55", Value = "{\"messageType\":123}" }
        };

        var fakeConsumer = new FakeSequenceConsumer([poisonRecord]);

        var worker = new RentalItemCompletedKafkaWorker(
            sp, config, NullLogger<RentalItemCompletedKafkaWorker>.Instance, _ => fakeConsumer);

        using var cts = new CancellationTokenSource();

        fakeConsumer.OnSeekCalled = () => cts.Cancel(); // Cancel once retry seek happens

        await worker.ExecuteWorkerAsync(cts.Token);

        // Assert: Offset was NEVER committed, and seek back was called
        Assert.Empty(fakeConsumer.CommittedOffsets);
        Assert.True(fakeConsumer.SeekWasCalled);
        Assert.Equal(55, fakeConsumer.LastSoughtOffset);
    }

    private sealed class FakeSequenceConsumer : IConsumer<string, string>
    {
        private readonly Queue<ConsumeResult<string, string>> _queue;
        public List<long> CommittedOffsets { get; } = [];
        public bool SeekWasCalled { get; private set; }
        public long LastSoughtOffset { get; private set; }
        public Action? OnCommitCalled { get; set; }
        public Action? OnSeekCalled { get; set; }

        public FakeSequenceConsumer(IEnumerable<ConsumeResult<string, string>> records)
        {
            _queue = new Queue<ConsumeResult<string, string>>(records);
        }

        public ConsumeResult<string, string>? Consume(TimeSpan timeout)
        {
            if (_queue.TryDequeue(out var result))
            {
                return result;
            }

            return null;
        }

        public void Commit(ConsumeResult<string, string> result)
        {
            CommittedOffsets.Add(result.Offset.Value);
            OnCommitCalled?.Invoke();
        }

        public void Seek(TopicPartitionOffset tpo)
        {
            SeekWasCalled = true;
            LastSoughtOffset = tpo.Offset.Value;
            OnSeekCalled?.Invoke();
        }

        public void Subscribe(string topic) { }
        public void Subscribe(IEnumerable<string> topics) { }
        public void Close() { }
        public void Dispose() { }

        // Interface stubs
        public Handle Handle => null!;
        public string Name => "FakeConsumer";
        public List<string> Subscription => ["rental.events.v1"];
        public List<TopicPartition> Assignment => [];
        public string MemberId => "fake-member";
        public void Unsubscribe() { }
        public void Assign(TopicPartition partition) { }
        public void Assign(TopicPartitionOffset partition) { }
        public void Assign(IEnumerable<TopicPartitionOffset> partitions) { }
        public void Assign(IEnumerable<TopicPartition> partitions) { }
        public void Unassign() { }
        public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions) { }
        public void IncrementalAssign(IEnumerable<TopicPartition> partitions) { }
        public void IncrementalUnassign(IEnumerable<TopicPartition> partitions) { }
        public ConsumeResult<string, string>? Consume(int millisecondsTimeout) => Consume(TimeSpan.FromMilliseconds(millisecondsTimeout));
        public ConsumeResult<string, string>? Consume(CancellationToken cancellationToken = default) => Consume(TimeSpan.Zero);
        public List<TopicPartitionOffset> Commit() => [];
        public void Commit(IEnumerable<TopicPartitionOffset> offsets) { }
        public void StoreOffset(ConsumeResult<string, string> result) { }
        public void StoreOffset(TopicPartitionOffset offset) { }
        public List<TopicPartitionOffset> Position(IEnumerable<TopicPartition> partitions) => [];
        public Offset Position(TopicPartition partition) => new(0);
        public IConsumerGroupMetadata ConsumerGroupMetadata => null!;
        public int AddBrokers(string brokers) => 0;
        public void SetSaslCredentials(string username, string password) { }
        public List<TopicPartitionOffset> Committed(TimeSpan timeout) => [];
        public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout) => [];
        public WatermarkOffsets GetWatermarkOffsets(TopicPartition partition) => new(0, 0);
        public WatermarkOffsets QueryWatermarkOffsets(TopicPartition partition, TimeSpan timeout) => new(0, 0);
        public List<TopicPartitionOffset> OffsetsForTimes(IEnumerable<TopicPartitionTimestamp> timestampsToSearch, TimeSpan timeout) => [];
        public void Pause(IEnumerable<TopicPartition> partitions) { }
        public void Resume(IEnumerable<TopicPartition> partitions) { }
    }

    private sealed class SpyDlqProducer : IKafkaProducer
    {
        private readonly bool _returnSuccess;
        public List<(string Topic, string Key, string Payload)> Messages { get; } = [];

        public SpyDlqProducer(bool returnSuccess = true) => _returnSuccess = returnSuccess;

        public Task<bool> ProduceAsync(string topic, string partitionKey, string payload, CancellationToken cancellationToken = default)
        {
            Messages.Add((topic, partitionKey, payload));
            return Task.FromResult(_returnSuccess);
        }
    }

    private sealed class MockRentalClient : IRentalServiceClient
    {
        private readonly RentalItemReviewSnapshotDto? _snapshot;

        public MockRentalClient(RentalItemReviewSnapshotDto? snapshot = null) => _snapshot = snapshot;

        public Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(Guid orderId, Guid itemId, CancellationToken cancellationToken = default)
        {
            if (_snapshot != null) return Task.FromResult(Result.Success(_snapshot));
            return Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(Dprental.ReviewService.Common.Error.NotFound("NOT_FOUND", "Snapshot not found")));
        }

        public Task<Result<ReconciliationBatchDto>> GetReconciliationBatchAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success(new ReconciliationBatchDto([], null, false)));
        }
    }
}
