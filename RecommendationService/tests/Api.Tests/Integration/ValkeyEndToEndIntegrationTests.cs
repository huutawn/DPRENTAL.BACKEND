using Dprental.RecommendationService.Api.Tests.TestHelpers;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class ValkeyEndToEndIntegrationTests : IClassFixture<PostgreSqlTestcontainerFixture>, IClassFixture<ValkeyTestcontainerFixture>
{
    private readonly PostgreSqlTestcontainerFixture _postgresFixture;
    private readonly ValkeyTestcontainerFixture _valkeyFixture;

    public ValkeyEndToEndIntegrationTests(
        PostgreSqlTestcontainerFixture postgresFixture,
        ValkeyTestcontainerFixture valkeyFixture)
    {
        _postgresFixture = postgresFixture;
        _valkeyFixture = valkeyFixture;
    }

    private sealed class SingleMultiplexerProvider : IValkeyConnectionProvider
    {
        private readonly IConnectionMultiplexer _mux;

        public SingleMultiplexerProvider(IConnectionMultiplexer mux)
        {
            _mux = mux;
        }

        public bool IsConnected => _mux.IsConnected;

        public Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IConnectionMultiplexer?>(_mux);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TopN_Seeding25UserTypes_And_EvictionOnScoreUpdate_Verified()
    {
        var db = _postgresFixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var userRepo = new UserEquipmentTypeInterestRepository(db);
            var listingRepo = new ListingPopularityRepository(db);
            var provider = new SingleMultiplexerProvider(_valkeyFixture.Multiplexer);
            var valkeyRepo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
            var publishingService = new SnapshotPublishingService(userRepo, listingRepo, valkeyRepo, NullLogger<SnapshotPublishingService>.Instance);

            var userId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            // 1. Seed 25 equipment types with scores 1 to 25
            var typeIds = new List<Guid>();
            for (short i = 1; i <= 25; i++)
            {
                var typeId = Guid.NewGuid();
                typeIds.Add(typeId);
                await userRepo.UpsertAsync(userId, typeId, i, now, CancellationToken.None);
            }

            // Publish user ranking
            var publishRes = await publishingService.PublishUserAsync(userId, CancellationToken.None);
            Assert.Equal(PublishOutcome.Published, publishRes.Outcome);

            // Read from Valkey ZSET directly
            var valkeyDb = _valkeyFixture.Multiplexer.GetDatabase();
            string userKey = ValkeyProjectionRepository.GetUserDataKey(userId);

            long zcard = await valkeyDb.SortedSetLengthAsync(userKey);
            Assert.Equal(20, zcard); // Top 20 strictly enforced

            var topEntries = await valkeyDb.SortedSetRangeByRankWithScoresAsync(userKey, 0, 19, Order.Descending);
            Assert.Equal(20, topEntries.Length);
            // Highest score should be 25, lowest score in top 20 should be 6
            Assert.Equal(25, (long)topEntries[0].Score);
            Assert.Equal(6, (long)topEntries[^1].Score);

            // Type with score 1 (typeIds[0]) was NOT included
            bool containsScore1 = topEntries.Any(e => string.Equals(e.Element.ToString(), typeIds[0].ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.False(containsScore1);

            // 2. Score update: boost typeIds[0] by +100 (now score 101)
            await userRepo.UpsertAsync(userId, typeIds[0], 100, now, CancellationToken.None);

            var republishRes = await publishingService.PublishUserAsync(userId, CancellationToken.None);
            Assert.Equal(PublishOutcome.Published, republishRes.Outcome);

            var updatedEntries = await valkeyDb.SortedSetRangeByRankWithScoresAsync(userKey, 0, 19, Order.Descending);
            Assert.Equal(20, updatedEntries.Length);
            Assert.Equal(typeIds[0].ToString("D").ToLowerInvariant(), updatedEntries[0].Element.ToString());
            Assert.Equal(101, (long)updatedEntries[0].Score);

            // The previous lowest item (score 6) should now be evicted!
            bool containsScore6 = updatedEntries.Any(e => (long)e.Score == 6);
            Assert.False(containsScore6);

            // Cleanup
            await valkeyDb.KeyDeleteAsync(userKey);
        }
    }

    [Fact]
    public async Task Top300_GlobalPopularListings_And_FlushRebuild_Verified()
    {
        var db = _postgresFixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var userRepo = new UserEquipmentTypeInterestRepository(db);
            var listingRepo = new ListingPopularityRepository(db);
            var provider = new SingleMultiplexerProvider(_valkeyFixture.Multiplexer);
            var valkeyRepo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
            var publishingService = new SnapshotPublishingService(userRepo, listingRepo, valkeyRepo, NullLogger<SnapshotPublishingService>.Instance);

            var valkeyDb = _valkeyFixture.Multiplexer.GetDatabase();
            var now = DateTimeOffset.UtcNow;

            // Seed 310 popular listings with scores
            for (short i = 1; i <= 310; i++)
            {
                await listingRepo.UpsertAsync(Guid.NewGuid(), i, now, CancellationToken.None);
            }

            // Also seed a user with 5 types
            var userId = Guid.NewGuid();
            for (short i = 1; i <= 5; i++)
            {
                await userRepo.UpsertAsync(userId, Guid.NewGuid(), i, now, CancellationToken.None);
            }

            // Rebuild cache
            var rebuildRes = await publishingService.RebuildCacheAsync(CancellationToken.None);
            Assert.True(rebuildRes.PublishedCount >= 2); // At least 1 global + 1 user
            Assert.Equal(0, rebuildRes.FailedCount);

            // Check global ZSET length
            long globalCount = await valkeyDb.SortedSetLengthAsync(ValkeyProjectionRepository.GlobalDataKey);
            Assert.Equal(300, globalCount);

            // Check user ZSET
            string userKey = ValkeyProjectionRepository.GetUserDataKey(userId);
            long userCount = await valkeyDb.SortedSetLengthAsync(userKey);
            Assert.Equal(5, userCount);

            // 3. Delete all rec:* keys and rebuild again to verify recovery from scratch
            await valkeyDb.KeyDeleteAsync(ValkeyProjectionRepository.GlobalDataKey);
            await valkeyDb.KeyDeleteAsync(userKey);

            Assert.False(await valkeyDb.KeyExistsAsync(ValkeyProjectionRepository.GlobalDataKey));
            Assert.False(await valkeyDb.KeyExistsAsync(userKey));

            var recoveryRes = await publishingService.RebuildCacheAsync(CancellationToken.None);
            Assert.Equal(0, recoveryRes.FailedCount);

            Assert.True(await valkeyDb.KeyExistsAsync(ValkeyProjectionRepository.GlobalDataKey));
            Assert.True(await valkeyDb.KeyExistsAsync(userKey));
            Assert.Equal(300, await valkeyDb.SortedSetLengthAsync(ValkeyProjectionRepository.GlobalDataKey));
            Assert.Equal(5, await valkeyDb.SortedSetLengthAsync(userKey));

            // Cleanup
            await valkeyDb.KeyDeleteAsync(ValkeyProjectionRepository.GlobalDataKey);
            await valkeyDb.KeyDeleteAsync(userKey);
        }
    }

    [Fact]
    public async Task TieBreak_AtTopNBoundary_PicksDescUuid()
    {
        var db = _postgresFixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var userRepo = new UserEquipmentTypeInterestRepository(db);
            var listingRepo = new ListingPopularityRepository(db);
            var provider = new SingleMultiplexerProvider(_valkeyFixture.Multiplexer);
            var valkeyRepo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
            var publishingService = new SnapshotPublishingService(userRepo, listingRepo, valkeyRepo, NullLogger<SnapshotPublishingService>.Instance);

            var userId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            // Seed 19 types with score 10
            for (int i = 0; i < 19; i++)
            {
                await userRepo.UpsertAsync(userId, Guid.NewGuid(), 10, now, CancellationToken.None);
            }

            // Seed 2 types with score 5 (tied at the rank 20 boundary)
            var lowerUuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var higherUuid = Guid.Parse("22222222-2222-2222-2222-222222222222");

            await userRepo.UpsertAsync(userId, lowerUuid, 5, now, CancellationToken.None);
            await userRepo.UpsertAsync(userId, higherUuid, 5, now, CancellationToken.None);

            await publishingService.PublishUserAsync(userId, CancellationToken.None);

            var valkeyDb = _valkeyFixture.Multiplexer.GetDatabase();
            string userKey = ValkeyProjectionRepository.GetUserDataKey(userId);

            var entries = await valkeyDb.SortedSetRangeByRankWithScoresAsync(userKey, 0, 19, Order.Descending);
            Assert.Equal(20, entries.Length);

            // The 20th item must be higherUuid because tie-break is DESC
            Assert.Equal(higherUuid.ToString("D").ToLowerInvariant(), entries[^1].Element.ToString());

            // lowerUuid was evicted
            bool containsLower = entries.Any(e => string.Equals(e.Element.ToString(), lowerUuid.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.False(containsLower);

            await valkeyDb.KeyDeleteAsync(userKey);
        }
    }

    [Fact]
    public async Task FlushBetweenReadAndWrite_CausesTokenMismatch_AndFailsGracefully()
    {
        var provider = new SingleMultiplexerProvider(_valkeyFixture.Multiplexer);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        var db = _valkeyFixture.Multiplexer.GetDatabase();

        var userId = Guid.NewGuid();
        var leaseRes = await repo.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Acquired, leaseRes.Status);

        // Simulate flush / lease key deletion before replace
        await db.KeyDeleteAsync(leaseRes.Lease!.LeaseKey);

        var outcome = await repo.ReplaceUserRankingAsync(leaseRes.Lease!, [new(Guid.NewGuid(), 10)], TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.LeaseLost, outcome);
    }
}
