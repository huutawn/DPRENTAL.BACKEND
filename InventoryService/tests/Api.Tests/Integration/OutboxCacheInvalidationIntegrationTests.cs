using Dprental.InventoryService.Api.Caching;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.Kafka.Contracts;
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
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

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

    private ValkeyReadCache CreateValkeyCache()
    {
        var options = Options.Create(new InventoryCacheOptions
        {
            Enabled = true,
            DynamicTtlSeconds = 15,
            MetadataTtlSeconds = 120,
            CatalogTtlSeconds = 120,
            TimeoutMs = 500,
            EnvironmentName = "development"
        });
        return new ValkeyReadCache(options, _valkey.Multiplexer, null, new InventoryCacheMetrics(), NullLogger<ValkeyReadCache>.Instance);
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
    public async Task OutboxPublisherWorker_PublishesKafka_WithoutCouplingToCache()
    {
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // 1. Stage and commit Outbox message
        var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(assetId, now);
        await using (var db = CreateDbContext())
        {
            db.OutboxMessages.Add(outboxMsg);
            await db.SaveChangesAsync();
        }

        // 2. Process batch via OutboxPublisherWorker (without cache dependency)
        var scopeFactory = CreateScopeFactory();
        var producer = new FakeKafkaProducer();
        var worker = new OutboxPublisherWorker(scopeFactory, producer);

        int processed = await worker.ProcessBatchAsync();
        Assert.True(processed >= 1);

        // 3. Verify Kafka publication
        Assert.Contains(producer.ProducedMessages, m => m.Key == assetId.ToString("D") && m.Topic == KafkaTopics.InventoryAssetEventsV1);

        // 4. Verify Outbox message marked as published in PostgreSQL
        await using (var verifyDb = CreateDbContext())
        {
            var msg = await verifyDb.OutboxMessages.FirstAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msg.PublishedAt);
        }
    }

    [Fact]
    public async Task CacheInvalidationWorker_ClaimsAndExecutesJob_RotatesValkeyGenerationToken()
    {
        await _valkey.FlushDbAsync();
        using var valkeyCache = CreateValkeyCache();
        var scope = "listing:test-inval";

        // 1. Initial read creates generation token
        var r1 = await valkeyCache.GetAsync<string>(scope, "detail", "hash");
        var token1 = r1.GenerationToken;
        Assert.False(string.IsNullOrWhiteSpace(token1));

        // 2. Stage invalidation job directly into database
        var job = CacheInvalidationJob.Create(Guid.NewGuid(), new[] { scope }, DateTimeOffset.UtcNow);
        await using (var db = CreateDbContext())
        {
            db.CacheInvalidationJobs.Add(job);
            await db.SaveChangesAsync();
        }

        // 3. Run CacheInvalidationWorker batch
        var scopeFactory = CreateScopeFactory();
        var worker = new CacheInvalidationWorker(scopeFactory, valkeyCache);
        int claimed = await worker.ProcessBatchAsync();
        Assert.True(claimed >= 1);

        // 4. Verify job is marked as Completed
        await using (var verifyDb = CreateDbContext())
        {
            var completedJob = await verifyDb.CacheInvalidationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(CacheInvalidationJob.StatusCompleted, completedJob.Status);
            Assert.NotNull(completedJob.ProcessedAt);
            Assert.Null(completedJob.LockLeaseToken);
        }

        // 5. Verify Valkey generation token changed!
        var r2 = await valkeyCache.GetAsync<string>(scope, "detail", "hash");
        var token2 = r2.GenerationToken;
        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public async Task Mutation_StagesCacheInvalidationJob_CommittedInSameTransaction()
    {
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await using (var initDb = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestAssetAsync(initDb, assetId: assetId, ownerId: ownerId, now: FixedNow);
        }

        var scopeFactory = CreateScopeFactory();
        await using var scope = scopeFactory.CreateAsyncScope();
        var assetRepo = scope.ServiceProvider.GetRequiredService<IInventoryAssetRepository>();
        var listingRepo = scope.ServiceProvider.GetRequiredService<IEquipmentListingRepository>();
        var tx = scope.ServiceProvider.GetRequiredService<IInventoryWriteTransaction>();

        var calendarService = new CalendarService(assetRepo, listingRepo, tx);
        var caller = new TestCaller { UserId = ownerId };

        var req = new CreateCalendarBlockRequest
        {
            StartDate = new DateOnly(2026, 10, 20),
            EndDate = new DateOnly(2026, 10, 25),
            Reason = CalendarBlockReason.Maintenance,
            Note = "Test maintenance"
        };

        // Mutation creates block and commits in transaction
        var result = await calendarService.CreateCalendarBlockAsync(assetId, req, caller);
        Assert.True(result.IsSuccess);

        // Verify that a cache invalidation job was staged and committed!
        await using var verifyDb = CreateDbContext();
        var job = await verifyDb.CacheInvalidationJobs
            .FirstOrDefaultAsync(j => j.Status == CacheInvalidationJob.StatusPending &&
                                      j.Scopes.Contains($"asset:{assetId:D}"));

        Assert.NotNull(job);
        Assert.Contains($"asset:{assetId:D}", job.Scopes);
        Assert.Contains("public-discovery", job.Scopes);
    }

    [Fact]
    public async Task ValkeyOutage_CacheInvalidationWorker_RetriesWithBackoff_JobNotLost()
    {
        // Multiplexer pointing to unreachable endpoint
        var brokenConfig = ConfigurationOptions.Parse("127.0.0.1:59999");
        brokenConfig.AbortOnConnectFail = false;
        brokenConfig.ConnectTimeout = 100;
        brokenConfig.SyncTimeout = 100;
        brokenConfig.AsyncTimeout = 100;
        var brokenMux = await ConnectionMultiplexer.ConnectAsync(brokenConfig);

        var brokenOptions = Options.Create(new InventoryCacheOptions
        {
            Enabled = true,
            DynamicTtlSeconds = 15,
            TimeoutMs = 100,
            EnvironmentName = "development"
        });

        using var brokenCache = new ValkeyReadCache(brokenOptions, brokenMux, null, new InventoryCacheMetrics(), NullLogger<ValkeyReadCache>.Instance);

        // Stage job
        var job = CacheInvalidationJob.Create(Guid.NewGuid(), ["asset:broken-test"], DateTimeOffset.UtcNow);
        await using (var db = CreateDbContext())
        {
            db.CacheInvalidationJobs.Add(job);
            await db.SaveChangesAsync();
        }

        var scopeFactory = CreateScopeFactory();
        var worker = new CacheInvalidationWorker(scopeFactory, brokenCache);

        // Process batch: Valkey call fails
        int count = await worker.ProcessBatchAsync();
        Assert.Equal(1, count);

        // Verify job was NOT lost; status remains Pending with retry count incremented and future NextAttemptAt!
        await using (var verifyDb = CreateDbContext())
        {
            var retriedJob = await verifyDb.CacheInvalidationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(CacheInvalidationJob.StatusPending, retriedJob.Status);
            Assert.Equal(1, retriedJob.RetryCount);
            Assert.True(retriedJob.NextAttemptAt > DateTimeOffset.UtcNow);
            Assert.NotNull(retriedJob.LastError);
        }
    }
}
