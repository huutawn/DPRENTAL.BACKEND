using System.Data;
using Dprental.InventoryService.Api.Caching;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

[Collection("ValkeyIntegrationCollection")]
public sealed class OutboxCacheInvalidationIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly string _postgresConn;
    private readonly ValkeyFixture _valkey;
    private const string KafkaBootstrapServers = "127.0.0.1:29092";
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] MonthKey202610Array = ["202610"];

    public OutboxCacheInvalidationIntegrationTests(PostgresFixture postgresFixture, ValkeyFixture valkeyFixture)
    {
        _postgresConn = postgresFixture.ConnectionString;
        _valkey = valkeyFixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgresConn, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_postgresConn);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IServiceScopeFactory>();
    }

    private ValkeyInventoryAvailabilityCache CreateValkeyCache()
    {
        var options = Options.Create(new InventoryCacheOptions
        {
            Enabled = true,
            TtlSeconds = 60,
            TimeoutMs = 500
        });
        return new ValkeyInventoryAvailabilityCache(options, _valkey.Multiplexer, null, NullLogger<ValkeyInventoryAvailabilityCache>.Instance);
    }

    private sealed class FakeKafkaProducer : IKafkaProducer
    {
        public List<(string Topic, string Key, string Payload)> ProducedMessages { get; } = new();

        public Task ProduceEnvelopeAsync<T>(string topic, MessageEnvelope<T> envelope, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        {
            ProducedMessages.Add((topic, envelope.KafkaKey, KafkaEnvelopeSerializer.Serialize(envelope)));
            return Task.CompletedTask;
        }

        public Task ProduceRawAsync(string topic, string key, string payload, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        {
            ProducedMessages.Add((topic, key, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCache : IInventoryAvailabilityCache
    {
        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<string, CachedMonthRanges?>> GetMonthRangesBulkAsync(Guid assetId, IReadOnlyList<string> monthKeys, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            throw new RedisException("Simulated network failure");

        public Task SetMonthRangesAsync(Guid assetId, string monthKey, IReadOnlyList<BusyRangeDto> ranges, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new RedisException("Simulated network failure");

        public Task InvalidateAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            throw new RedisException("Simulated network failure");
    }

    private sealed class TestCaller : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsHuman => true;
        public bool IsService => false;
        public bool IsAdmin => false;
        public Guid? UserId { get; set; }
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
        public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string>();
        public IReadOnlySet<string> Roles { get; set; } = new HashSet<string>();
        public Guid? OrganizationId { get; set; }
    }

    [Fact]
    public async Task CommittedEvent_DeletesAllMonthKeys_AndPublishesKafka()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var valkeyCache = CreateValkeyCache();
        var now = DateTimeOffset.UtcNow;

        // 1. Setup cached months in Valkey
        var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
        await valkeyCache.SetMonthRangesAsync(assetId, "202610", ranges, now.AddMinutes(5));
        await valkeyCache.SetMonthRangesAsync(assetId, "202611", ranges, now.AddMinutes(5));

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202611"));
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:months"));

        // 2. Stage and commit Outbox message
        var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(assetId, now);
        await using (var db = CreateDbContext())
        {
            db.OutboxMessages.Add(outboxMsg);
            await db.SaveChangesAsync();
        }

        // 3. Process batch via worker
        var scopeFactory = CreateScopeFactory();
        var producer = new FakeKafkaProducer();
        var worker = new OutboxPublisherWorker(scopeFactory, producer, valkeyCache);

        int processed = await worker.ProcessBatchAsync();
        Assert.True(processed >= 1);

        // 4. Verify Kafka publication
        Assert.Contains(producer.ProducedMessages, m => m.Key == assetId.ToString("D") && m.Topic == KafkaTopics.InventoryAssetEventsV1);

        // 5. Verify Valkey cache was invalidated!
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202611"));
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:months"));

        // 6. Verify Outbox message marked as published in PostgreSQL
        await using (var verifyDb = CreateDbContext())
        {
            var msg = await verifyDb.OutboxMessages.FirstAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msg.PublishedAt);
        }
    }

    [Fact]
    public async Task CommittedEvent_WithRealKafkaBroker_InvalidatesValkeyCache_AndPublishesSuccessfully()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var valkeyCache = CreateValkeyCache();
        var now = DateTimeOffset.UtcNow;

        var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
        await valkeyCache.SetMonthRangesAsync(assetId, "202610", ranges, now.AddMinutes(5));

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));

        var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(assetId, now);
        await using (var db = CreateDbContext())
        {
            db.OutboxMessages.Add(outboxMsg);
            await db.SaveChangesAsync();
        }

        // Tạo real Kafka producer kết nối tới 127.0.0.1:29092
        var kafkaOpts = Options.Create(new KafkaOptions
        {
            BootstrapServers = KafkaBootstrapServers,
            ClientId = "test-real-kafka-worker",
            DeliveryTimeoutMs = 10000
        });

        using var realProducer = new KafkaProducer(kafkaOpts);
        var scopeFactory = CreateScopeFactory();
        var worker = new OutboxPublisherWorker(scopeFactory, realProducer, valkeyCache);

        int processed = await worker.ProcessBatchAsync();
        Assert.True(processed >= 1);

        // Verify Valkey cache was invalidated
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));

        // Verify Outbox message acknowledged by real Kafka broker
        await using (var verifyDb = CreateDbContext())
        {
            var msg = await verifyDb.OutboxMessages.FirstAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msg.PublishedAt);
        }
    }

    [Fact]
    public async Task RollbackOrUncommittedTransaction_DoesNotInvalidateCache()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var valkeyCache = CreateValkeyCache();
        var now = DateTimeOffset.UtcNow;

        var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
        await valkeyCache.SetMonthRangesAsync(assetId, "202610", ranges, now.AddMinutes(5));

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));

        // Uncommitted / rolled back transaction
        var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(assetId, now);
        await using (var db = CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            db.OutboxMessages.Add(outboxMsg);
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        // Run worker
        var scopeFactory = CreateScopeFactory();
        var producer = new FakeKafkaProducer();
        var worker = new OutboxPublisherWorker(scopeFactory, producer, valkeyCache);

        int processed = await worker.ProcessBatchAsync();
        Assert.Equal(0, processed);

        // Cache remains intact!
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
    }

    [Fact]
    public async Task InvalidationFailure_PreservesDatabaseSuccess_And_KafkaPublication()
    {
        var assetId = Guid.NewGuid();
        var throwingCache = new ThrowingCache();
        var now = DateTimeOffset.UtcNow;

        var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(assetId, now);
        await using (var db = CreateDbContext())
        {
            db.OutboxMessages.Add(outboxMsg);
            await db.SaveChangesAsync();
        }

        var scopeFactory = CreateScopeFactory();
        var producer = new FakeKafkaProducer();
        var worker = new OutboxPublisherWorker(scopeFactory, producer, throwingCache);

        // Worker should NOT crash when cache invalidation fails
        int processed = await worker.ProcessBatchAsync();
        Assert.True(processed >= 1);

        // Kafka message was successfully produced
        Assert.Contains(producer.ProducedMessages, m => m.Key == assetId.ToString("D") && m.Topic == KafkaTopics.InventoryAssetEventsV1);

        // Outbox record is marked published in DB
        await using (var verifyDb = CreateDbContext())
        {
            var msg = await verifyDb.OutboxMessages.FirstAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msg.PublishedAt);
        }
    }

    [Fact]
    public async Task RealMutationViaCalendarService_StagesOutboxAndInvalidatesCache()
    {
        await _valkey.FlushDbAsync();
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext())
        {
            var asset = InventoryAsset.Create(assetId, ownerId, Guid.NewGuid(), "MutationAsset", 1, "{}", FixedNow).Value;
            seedDb.Assets.Add(asset);
            await seedDb.SaveChangesAsync();
        }

        var valkeyCache = CreateValkeyCache();
        var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
        await valkeyCache.SetMonthRangesAsync(assetId, "202610", ranges, DateTimeOffset.UtcNow.AddMinutes(5));

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));

        // Thực hiện mutation qua CalendarService
        var scopeFactory = CreateScopeFactory();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var writeTx = scope.ServiceProvider.GetRequiredService<IInventoryWriteTransaction>();
            var assetRepo = scope.ServiceProvider.GetRequiredService<IInventoryAssetRepository>();
            var calService = new CalendarService(assetRepo, writeTx);

            var caller = new TestCaller { UserId = ownerId };
            var request = new CreateCalendarBlockRequest
            {
                StartDate = new DateOnly(2026, 10, 15),
                EndDate = new DateOnly(2026, 10, 18),
                Reason = CalendarBlockReason.Maintenance,
                Note = "Routine maintenance"
            };

            var res = await calService.CreateCalendarBlockAsync(assetId, request, caller);
            Assert.True(res.IsSuccess);
        }

        // Worker claim và invalidate cache
        var producer = new FakeKafkaProducer();
        var worker = new OutboxPublisherWorker(scopeFactory, producer, valkeyCache);
        int processed = await worker.ProcessBatchAsync();
        Assert.True(processed >= 1);

        // Cache cho asset đã được invalidate!
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
    }

    [Fact]
    public async Task BarrierFillRace_DelayedFillSelfExpiresByInitialDeadline()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var valkeyCache = CreateValkeyCache();

        var t0 = DateTimeOffset.UtcNow;
        // Deadline ban đầu là T0 + 2s
        var deadline = t0.AddSeconds(2);
        var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };

        // 1. Thread 2: Mutation diễn ra và invalidate cache
        await valkeyCache.InvalidateAssetAsync(assetId);

        // 2. Thread 1: Delayed fill ghi lại kết quả snapshot nhưng neo theo deadline ban đầu T0 + 2s
        await valkeyCache.SetMonthRangesAsync(assetId, "202610", ranges, deadline);

        // Đọc tại T0 -> hit
        var r1 = await valkeyCache.GetMonthRangesBulkAsync(assetId, MonthKey202610Array, t0);
        Assert.NotNull(r1["202610"]);

        // Khi thời gian vượt qua deadline ban đầu -> tự hết hạn theo deadline mà không cần background cleanup
        var r2 = await valkeyCache.GetMonthRangesBulkAsync(assetId, MonthKey202610Array, t0.AddSeconds(3));
        Assert.Null(r2["202610"]);
    }

    [Fact]
    public async Task ReplayIdempotent_RepeatedInvalidation_Succeeds()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var valkeyCache = CreateValkeyCache();

        // Calling Invalidate multiple times does not throw
        await valkeyCache.InvalidateAssetAsync(assetId);
        await valkeyCache.InvalidateAssetAsync(assetId);
        await valkeyCache.InvalidateAssetAsync(assetId);

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:months"));
    }
}
