using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class BehaviorOutboxPersistenceTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    public Task InitializeAsync() => _postgres.InitializeAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync();
    private InventoryServiceDbContext Context() => new(new DbContextOptionsBuilder<InventoryServiceDbContext>()
        .UseNpgsql(_postgres.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);

    [Fact]
    public async Task Cleanup_UsesRealSql_RespectsCutoffBatchAndOtherStates()
    {
        await using var db = Context();
        var store = new OutboxStore(db);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var expired = Enumerable.Range(0, 502).Select(i => Message(KafkaTopics.InventoryBehaviorEventsV1, cutoff.AddSeconds(-503 + i))).ToArray();
        var kept = new[]
        {
            Message(KafkaTopics.InventoryBehaviorEventsV1, cutoff),
            Message(KafkaTopics.InventoryBehaviorEventsV1, cutoff.AddDays(1)),
            Message(KafkaTopics.InventoryAllocationEventsV1, cutoff.AddDays(-1)),
            OutboxMessage.Create(Guid.NewGuid(), KafkaTopics.InventoryBehaviorEventsV1, "pending", "{}", cutoff.AddDays(-1)),
            OutboxMessage.Create(Guid.NewGuid(), KafkaTopics.InventoryBehaviorEventsV1, "failed", "{}", cutoff.AddDays(-1))
        };
        for (int i = 0; i < 10; i++) kept[4].RecordFailure("test", cutoff);
        db.OutboxMessages.AddRange(expired.Concat(kept));
        await db.SaveChangesAsync();
        Assert.Equal(500, await store.CleanupPublishedBehaviorMessagesAsync(cutoff, 500));
        var remaining = await db.OutboxMessages.AsNoTracking().Select(m => m.Id).ToListAsync();
        Assert.All(expired.Take(500), m => Assert.DoesNotContain(m.Id, remaining));
        Assert.All(expired.Skip(500).Concat(kept), m => Assert.Contains(m.Id, remaining));
        Assert.Equal(2, await store.CleanupPublishedBehaviorMessagesAsync(cutoff, 500));
        Assert.Equal(0, await store.CleanupPublishedBehaviorMessagesAsync(cutoff, 500));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CleanupPublishedBehaviorMessagesAsync(cutoff, 500, cts.Token));
        var index = await db.Database.SqlQueryRaw<string>("""
            SELECT indexdef AS "Value" FROM pg_indexes
            WHERE schemaname = 'inventory' AND indexname = 'ix_outbox_messages_behavior_cleanup'
            """).SingleAsync();
        Assert.Contains("published_at, id", index, StringComparison.Ordinal);
        Assert.Contains("published_at IS NOT NULL", index, StringComparison.Ordinal);
        Assert.Contains(KafkaTopics.InventoryBehaviorEventsV1, index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_CleansAtMostOncePerMinute_UsingRealStore()
    {
        var clock = new MutableRecommendationClock(DateTimeOffset.UtcNow);
        var services = new ServiceCollection().AddLogging();
        services.AddInventoryServiceDomainRepositories(_postgres.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        var worker = new OutboxPublisherWorker(provider.GetRequiredService<IServiceScopeFactory>(), new UnusedProducer(), timeProvider: clock);
        await using var db = Context();
        var first = Message(KafkaTopics.InventoryBehaviorEventsV1, clock.Now.AddDays(-8));
        db.OutboxMessages.Add(first);
        await db.SaveChangesAsync();
        Assert.Equal(1, await worker.TryCleanupBehaviorMessagesAsync());
        var second = Message(KafkaTopics.InventoryBehaviorEventsV1, clock.Now.AddDays(-8));
        db.OutboxMessages.Add(second);
        await db.SaveChangesAsync();
        Assert.Equal(0, await worker.TryCleanupBehaviorMessagesAsync());
        Assert.True(await db.OutboxMessages.AnyAsync(m => m.Id == second.Id));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Equal(1, await worker.TryCleanupBehaviorMessagesAsync());
        worker.Dispose();
    }

    private static OutboxMessage Message(string topic, DateTimeOffset publishedAt)
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), topic, Guid.NewGuid().ToString("D"), "{}", publishedAt);
        message.MarkAsPublished(publishedAt);
        return message;
    }

    private sealed class UnusedProducer : IKafkaProducer
    {
        public Task ProduceRawAsync(string topic, string kafkaKey, string payloadJson, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ProduceEnvelopeAsync<T>(string topic, MessageEnvelope<T> envelope, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
