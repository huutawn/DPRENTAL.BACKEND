using Dprental.RecommendationService.Api.Tests.TestHelpers;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class ValkeyRecoveryAndIntegrationTests : IClassFixture<ValkeyTestcontainerFixture>
{
    private readonly ValkeyTestcontainerFixture _fixture;

    public ValkeyRecoveryAndIntegrationTests(ValkeyTestcontainerFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class DynamicSwitchableValkeyConnectionProvider : IValkeyConnectionProvider
    {
        private IConnectionMultiplexer? _realMultiplexer;

        public bool IsSimulatingOutage { get; set; } = true;

        public void AttachRealMultiplexer(IConnectionMultiplexer mux)
        {
            _realMultiplexer = mux;
            IsSimulatingOutage = false;
        }

        public bool IsConnected => !IsSimulatingOutage && _realMultiplexer != null && _realMultiplexer.IsConnected;

        public Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSimulatingOutage)
            {
                return Task.FromResult<IConnectionMultiplexer?>(null);
            }

            return Task.FromResult<IConnectionMultiplexer?>(_realMultiplexer);
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
    public async Task ValkeyRecovery_OutageDoesNotCrash_RecoversWhenValkeyBecomesAvailable()
    {
        var provider = new DynamicSwitchableValkeyConnectionProvider();
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);

        // 1. Initial State: Valkey is down / outage
        provider.IsSimulatingOutage = true;
        var acquire1 = await repo.TryAcquireGlobalLeaseAsync(CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Unavailable, acquire1.Status);

        // 2. Recovery: Attach real multiplexer without restarting
        provider.AttachRealMultiplexer(_fixture.Multiplexer);
        var acquire2 = await repo.TryAcquireGlobalLeaseAsync(CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Acquired, acquire2.Status);
        Assert.NotNull(acquire2.Lease);

        var outcome = await repo.ReplaceGlobalRankingAsync(acquire2.Lease!, [], TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Published, outcome);
    }

    [Fact]
    public async Task Concurrency_TwoPublishersSameTarget_OneAcquires_OtherGetsBusy()
    {
        var provider = new SingleMultiplexerProvider(_fixture.Multiplexer);
        var repo1 = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        var repo2 = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);

        var userId = Guid.NewGuid();

        // 1. First writer acquires lease
        var result1 = await repo1.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Acquired, result1.Status);
        Assert.NotNull(result1.Lease);

        try
        {
            // 2. Second writer attempts same target concurrently -> Busy
            var result2 = await repo2.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
            Assert.Equal(LeaseAcquireStatus.Busy, result2.Status);
        }
        finally
        {
            await repo1.ReleaseLeaseAsync(result1.Lease!, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Concurrency_TwoDifferentUsers_DoNotBlockEachOther()
    {
        var provider = new SingleMultiplexerProvider(_fixture.Multiplexer);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var leaseA = await repo.TryAcquireUserLeaseAsync(userA, CancellationToken.None);
        var leaseB = await repo.TryAcquireUserLeaseAsync(userB, CancellationToken.None);

        Assert.Equal(LeaseAcquireStatus.Acquired, leaseA.Status);
        Assert.Equal(LeaseAcquireStatus.Acquired, leaseB.Status);

        await repo.ReleaseLeaseAsync(leaseA.Lease!, CancellationToken.None);
        await repo.ReleaseLeaseAsync(leaseB.Lease!, CancellationToken.None);
    }

    [Fact]
    public async Task WriterExpiredLease_CannotOverwriteUsingOldToken()
    {
        var provider = new SingleMultiplexerProvider(_fixture.Multiplexer);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        var db = _fixture.Multiplexer.GetDatabase();

        var userId = Guid.NewGuid();
        var leaseRes = await repo.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Acquired, leaseRes.Status);
        var lease = leaseRes.Lease!;

        // Simulate lease expiration by deleting lease key or overwriting with new writer's token
        await db.KeyDeleteAsync(lease.LeaseKey);

        var items = new List<UserEquipmentTypeInterestItem>
        {
            new(Guid.NewGuid(), 100)
        };

        var outcome = await repo.ReplaceUserRankingAsync(lease, items, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.LeaseLost, outcome);
    }

    [Fact]
    public async Task CleanupOfOldWriter_DoesNotDeleteNewLease()
    {
        var provider = new SingleMultiplexerProvider(_fixture.Multiplexer);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        var db = _fixture.Multiplexer.GetDatabase();

        var userId = Guid.NewGuid();
        var leaseRes1 = await repo.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
        var lease1 = leaseRes1.Lease!;

        // Writer 2 takes over the lease with a new token
        string token2 = Guid.NewGuid().ToString("N");
        await db.StringSetAsync(lease1.LeaseKey, token2);

        // Writer 1 attempts cleanup
        bool released = await repo.ReleaseLeaseAsync(lease1, CancellationToken.None);
        Assert.False(released);

        // Verify Writer 2's token is still intact
        var currentToken = await db.StringGetAsync(lease1.LeaseKey);
        Assert.Equal(token2, currentToken.ToString());

        await db.KeyDeleteAsync(lease1.LeaseKey);
    }

    [Fact]
    public async Task ReplaceRanking_ReplacesOldJsonString_WithoutWrongType_AndReadsDescWithScores()
    {
        var provider = new SingleMultiplexerProvider(_fixture.Multiplexer);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        var db = _fixture.Multiplexer.GetDatabase();

        var userId = Guid.NewGuid();
        string dataKey = ValkeyProjectionRepository.GetUserDataKey(userId);

        // Seed old JSON string at dataKey
        await db.StringSetAsync(dataKey, "{\"version\":1,\"generatedAt\":\"2026-10-19T10:00:00Z\",\"items\":[]}");

        var leaseRes = await repo.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Acquired, leaseRes.Status);

        var typeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var typeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var items = new List<UserEquipmentTypeInterestItem>
        {
            new(typeA, 50),
            new(typeB, 50)
        };

        // Replace ranking
        var outcome = await repo.ReplaceUserRankingAsync(leaseRes.Lease!, items, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Published, outcome);

        // Verify type is zset
        var keyType = await db.KeyTypeAsync(dataKey);
        Assert.Equal(RedisType.SortedSet, keyType);

        // Verify TTL is set (approx 300s)
        var ttl = await db.KeyTimeToLiveAsync(dataKey);
        Assert.NotNull(ttl);
        Assert.True(ttl.Value.TotalSeconds > 250 && ttl.Value.TotalSeconds <= 300);

        // Read with ZRANGE ... REV WITHSCORES
        var zrange = await db.SortedSetRangeByRankWithScoresAsync(dataKey, 0, 19, Order.Descending);
        Assert.Equal(2, zrange.Length);

        // Tie-break UUID DESC puts typeB before typeA
        Assert.Equal(typeB.ToString("D").ToLowerInvariant(), zrange[0].Element.ToString());
        Assert.Equal(50, (long)zrange[0].Score);
        Assert.Equal(typeA.ToString("D").ToLowerInvariant(), zrange[1].Element.ToString());
        Assert.Equal(50, (long)zrange[1].Score);

        // Cleanup
        await db.KeyDeleteAsync(dataKey);
    }

    [Fact]
    public async Task EmptyRanking_DeletesKey_AndLeavesItNonExistent()
    {
        var provider = new SingleMultiplexerProvider(_fixture.Multiplexer);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        var db = _fixture.Multiplexer.GetDatabase();

        var userId = Guid.NewGuid();
        string dataKey = ValkeyProjectionRepository.GetUserDataKey(userId);

        // Seed some initial data
        await db.SortedSetAddAsync(dataKey, "some-member", 100);

        var leaseRes = await repo.TryAcquireUserLeaseAsync(userId, CancellationToken.None);
        Assert.Equal(LeaseAcquireStatus.Acquired, leaseRes.Status);

        // Replace with empty ranking
        var outcome = await repo.ReplaceUserRankingAsync(leaseRes.Lease!, [], TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Published, outcome);

        // Key should not exist
        bool exists = await db.KeyExistsAsync(dataKey);
        Assert.False(exists);
    }

    [Fact]
    public async Task AclPolicy_EnforcesNamespaceAndAdministrativeSeparation()
    {
        var adminDb = _fixture.Multiplexer.GetDatabase();

        // Configure test ACL users dynamically on the testcontainer
        // Recommendation user: ~rec:* +get +set +del +expire +ttl +zadd +zrange +zcard +eval +evalsha
        string recPassword = "RecTestPassword123!";
        string invPassword = "InvTestPassword123!";

        await adminDb.ExecuteAsync("ACL", "SETUSER", "test_rec_user", "on", $">{recPassword}",
            "~rec:*", "&*", "resetchannels", "-@all",
            "+ping", "+info", "+client", "+command", "+select", "+hello", "+quit", "+time", "+echo",
            "+get", "+set", "+del", "+unlink", "+expire", "+ttl", "+zadd", "+zrange", "+zcard", "+eval", "+evalsha", "+script");

        // Inventory user: read-only on ~rec:*
        await adminDb.ExecuteAsync("ACL", "SETUSER", "test_inv_user", "on", $">{invPassword}",
            "~rec:*", "&*", "resetchannels", "-@all",
            "+ping", "+info", "+client", "+select", "+hello", "+quit", "+echo",
            "+zrange", "+zrevrange", "+zcard", "+get", "+ttl");

        var endpoint = _fixture.ConnectionString.Split(',')[0];

        // 1. Test Recommendation User
        var recOptions = ConfigurationOptions.Parse(endpoint);
        recOptions.User = "test_rec_user";
        recOptions.Password = recPassword;
        recOptions.AbortOnConnectFail = false;
        recOptions.AllowAdmin = true;

        using var recMux = await ConnectionMultiplexer.ConnectAsync(recOptions);
        var recDb = recMux.GetDatabase();

        // Allowed to write rec:*
        bool recSetOk = await recDb.StringSetAsync("rec:v1:test:allowed", "val");
        Assert.True(recSetOk);
        await recDb.KeyDeleteAsync("rec:v1:test:allowed");

        // Blocked outside namespace
        await Assert.ThrowsAnyAsync<RedisException>(async () =>
            await recDb.StringSetAsync("other:namespace:key", "val"));

        // Blocked from administrative commands (FLUSHDB)
        await Assert.ThrowsAnyAsync<RedisException>(async () =>
            await recDb.ExecuteAsync("FLUSHDB"));

        // 2. Test Inventory User
        var invOptions = ConfigurationOptions.Parse(endpoint);
        invOptions.User = "test_inv_user";
        invOptions.Password = invPassword;
        invOptions.AbortOnConnectFail = false;

        using var invMux = await ConnectionMultiplexer.ConnectAsync(invOptions);
        var invDb = invMux.GetDatabase();

        // Seed data via admin
        await adminDb.SortedSetAddAsync("rec:v1:global:popular-listings", "listing-1", 100);

        // Inventory can read ZSET
        var readResult = await invDb.SortedSetRangeByRankWithScoresAsync("rec:v1:global:popular-listings", 0, 10, Order.Descending);
        Assert.Single(readResult);

        // Inventory blocked from write / delete
        await Assert.ThrowsAnyAsync<RedisException>(async () =>
            await invDb.SortedSetAddAsync("rec:v1:global:popular-listings", "listing-2", 200));

        await Assert.ThrowsAnyAsync<RedisException>(async () =>
            await invDb.KeyDeleteAsync("rec:v1:global:popular-listings"));

        // Cleanup
        await adminDb.KeyDeleteAsync("rec:v1:global:popular-listings");
        await adminDb.ExecuteAsync("ACL", "DELUSER", "test_rec_user");
        await adminDb.ExecuteAsync("ACL", "DELUSER", "test_inv_user");
    }
}
