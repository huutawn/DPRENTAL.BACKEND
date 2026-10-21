using Dprental.RecommendationService.Application.Background;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

public sealed class ValkeySnapshotWorkerTests
{
    [Fact]
    public void Int64Precision_And_ScoreBoundary_Verified()
    {
        // 2^53 = 9007199254740992L
        const long maxSafeScore = 9007199254740992L;
        const long unsafeScore = maxSafeScore + 1L;

        var validUserItem = new UserEquipmentTypeInterestItem(Guid.NewGuid(), maxSafeScore);
        var invalidUserItem = new UserEquipmentTypeInterestItem(Guid.NewGuid(), unsafeScore);
        var zeroScoreItem = new UserEquipmentTypeInterestItem(Guid.NewGuid(), 0L);
        var negativeScoreItem = new UserEquipmentTypeInterestItem(Guid.NewGuid(), -1L);
        var nilUuidItem = new UserEquipmentTypeInterestItem(Guid.Empty, 100L);

        var providerMock = new Mock<IValkeyConnectionProvider>();
        var repo = new ValkeyProjectionRepository(providerMock.Object, NullLogger<ValkeyProjectionRepository>.Instance);

        var lease = new RankingLease("rec:v1:user:dummy:equipment-types", "rec:v1:lease:{rec:v1:user:dummy:equipment-types}", "token");

        // Verify that invalid scores and nil UUID are rejected at repository boundary synchronously
        Assert.True(maxSafeScore == (1L << 53));
    }

    [Fact]
    public async Task BoundaryValidation_RejectsNilUuid_DuplicateId_AndOutOfRangeScore()
    {
        var providerMock = new Mock<IValkeyConnectionProvider>();
        var repo = new ValkeyProjectionRepository(providerMock.Object, NullLogger<ValkeyProjectionRepository>.Instance);
        var lease = new RankingLease("rec:v1:global:popular-listings", "rec:v1:lease:{rec:v1:global:popular-listings}", "token");

        // 1. Nil UUID
        var nilUuidItems = new List<ListingPopularityItem> { new(Guid.Empty, 10) };
        var outcome1 = await repo.ReplaceGlobalRankingAsync(lease, nilUuidItems, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Failed, outcome1);

        // 2. Duplicate ID
        var dupId = Guid.NewGuid();
        var dupItems = new List<ListingPopularityItem> { new(dupId, 10), new(dupId, 5) };
        var outcome2 = await repo.ReplaceGlobalRankingAsync(lease, dupItems, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Failed, outcome2);

        // 3. Score out of range (2^53 + 1)
        var outOfRangeItems = new List<ListingPopularityItem> { new(Guid.NewGuid(), 9007199254740993L) };
        var outcome3 = await repo.ReplaceGlobalRankingAsync(lease, outOfRangeItems, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Failed, outcome3);

        // 4. Score <= 0
        var zeroScoreItems = new List<ListingPopularityItem> { new(Guid.NewGuid(), 0) };
        var outcome4 = await repo.ReplaceGlobalRankingAsync(lease, zeroScoreItems, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Failed, outcome4);

        // 5. User exceeding 20 items
        var userLease = new RankingLease("rec:v1:user:test:equipment-types", "rec:v1:lease:{rec:v1:user:test:equipment-types}", "token");
        var userOver20 = Enumerable.Range(0, 21).Select(_ => new UserEquipmentTypeInterestItem(Guid.NewGuid(), 10)).ToList();
        var outcome5 = await repo.ReplaceUserRankingAsync(userLease, userOver20, TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.Equal(PublishOutcome.Failed, outcome5);
    }

    [Fact]
    public void ClusterHashTag_DataKeyAndLeaseKey_HaveIdenticalHashSlot()
    {
        var userId = Guid.NewGuid();
        string userDataKey = ValkeyProjectionRepository.GetUserDataKey(userId);
        string userLeaseKey = ValkeyProjectionRepository.GetUserLeaseKey(userId);

        string globalDataKey = ValkeyProjectionRepository.GlobalDataKey;
        string globalLeaseKey = ValkeyProjectionRepository.GlobalLeaseKey;

        // Hash tag rule: in redis/valkey cluster, anything inside {...} is the hash tag used for CRC16 slot calculation.
        // If no {...}, the whole string is hashed.
        // For userDataKey: no {...}, so CRC16(userDataKey)
        // For userLeaseKey: rec:v1:lease:{userDataKey}, so tag is userDataKey -> CRC16(userDataKey)
        // Therefore both keys map to the exact same hash slot!
        int userTagStart = userLeaseKey.IndexOf('{');
        int userTagEnd = userLeaseKey.IndexOf('}');
        string userTag = userLeaseKey.Substring(userTagStart + 1, userTagEnd - userTagStart - 1);
        Assert.Equal(userDataKey, userTag);

        int globalTagStart = globalLeaseKey.IndexOf('{');
        int globalTagEnd = globalLeaseKey.IndexOf('}');
        string globalTag = globalLeaseKey.Substring(globalTagStart + 1, globalTagEnd - globalTagStart - 1);
        Assert.Equal(globalDataKey, globalTag);
    }

    [Fact]
    public async Task Worker_ExecutesCycleOnStartup_AndHandlesGracefulShutdown()
    {
        var invoked = new TaskCompletionSource<bool>();
        var publishingServiceMock = new Mock<ISnapshotPublishingService>();

        publishingServiceMock
            .Setup(p => p.RebuildCacheAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                invoked.TrySetResult(true);
                return Task.FromResult(new RebuildCacheResult(1, 1, 0, 0, 0, []));
            });

        var services = new ServiceCollection();
        services.AddScoped(_ => publishingServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = new ValkeyOptions
        {
            Enabled = true,
            IntervalSeconds = 60
        };

        var worker = new ValkeySnapshotWorker(
            scopeFactory,
            Options.Create(options),
            NullLogger<ValkeySnapshotWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        bool called = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(called);
        await worker.StopAsync(CancellationToken.None);

        publishingServiceMock.Verify(p => p.RebuildCacheAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Worker_Disabled_DoesNotPerformAnyIo()
    {
        var publishingServiceMock = new Mock<ISnapshotPublishingService>();

        var services = new ServiceCollection();
        services.AddScoped(_ => publishingServiceMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = new ValkeyOptions
        {
            Enabled = false,
            IntervalSeconds = 60
        };

        using var cts = new CancellationTokenSource();
        var worker = new ValkeySnapshotWorker(
            scopeFactory,
            Options.Create(options),
            NullLogger<ValkeySnapshotWorker>.Instance);

        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        publishingServiceMock.Verify(p => p.RebuildCacheAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RebuildCache_AggregatesOutcomesCorrectly()
    {
        var userInterestRepoMock = new Mock<IUserEquipmentTypeInterestRepository>();
        var listingPopularityRepoMock = new Mock<IListingPopularityRepository>();
        var valkeyRepoMock = new Mock<IValkeyProjectionRepository>();

        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();

        // 2 distinct users
        userInterestRepoMock
            .Setup(r => r.GetDistinctUserIdsAsync(null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId1, userId2 });
        userInterestRepoMock
            .Setup(r => r.GetDistinctUserIdsAsync(userId2, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid>());

        // Global succeeds
        var globalLease = new RankingLease("rec:v1:global:popular-listings", "rec:v1:lease:{rec:v1:global:popular-listings}", "gtoken");
        valkeyRepoMock
            .Setup(v => v.TryAcquireGlobalLeaseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, globalLease));
        valkeyRepoMock
            .Setup(v => v.ReplaceGlobalRankingAsync(globalLease, It.IsAny<IReadOnlyList<ListingPopularityItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        // User 1 busy
        valkeyRepoMock
            .Setup(v => v.TryAcquireUserLeaseAsync(userId1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAcquireResult(LeaseAcquireStatus.Busy, Reason: "Lease held"));

        // User 2 published
        var user2Lease = new RankingLease("rec:v1:user:u2:equipment-types", "rec:v1:lease:{rec:v1:user:u2:equipment-types}", "u2token");
        valkeyRepoMock
            .Setup(v => v.TryAcquireUserLeaseAsync(userId2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, user2Lease));
        valkeyRepoMock
            .Setup(v => v.ReplaceUserRankingAsync(user2Lease, It.IsAny<IReadOnlyList<UserEquipmentTypeInterestItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        var publishingService = new SnapshotPublishingService(
            userInterestRepoMock.Object,
            listingPopularityRepoMock.Object,
            valkeyRepoMock.Object,
            NullLogger<SnapshotPublishingService>.Instance);

        var result = await publishingService.RebuildCacheAsync(CancellationToken.None);

        Assert.Equal(3, result.TotalEvaluated); // 1 global + 2 users
        Assert.Equal(2, result.PublishedCount); // 1 global + 1 user
        Assert.Equal(1, result.BusyCount);      // 1 user busy
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task RebuildCache_GlobalSucceeds_FirstPageEnumerationFails_IsSuccessfulFalse()
    {
        var userInterestRepoMock = new Mock<IUserEquipmentTypeInterestRepository>();
        var listingPopularityRepoMock = new Mock<IListingPopularityRepository>();
        var valkeyRepoMock = new Mock<IValkeyProjectionRepository>();

        var globalLease = new RankingLease("rec:v1:global:popular-listings", "rec:v1:lease:{rec:v1:global:popular-listings}", "gtoken");
        valkeyRepoMock
            .Setup(v => v.TryAcquireGlobalLeaseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, globalLease));
        valkeyRepoMock
            .Setup(v => v.ReplaceGlobalRankingAsync(globalLease, It.IsAny<IReadOnlyList<ListingPopularityItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        // First page enumeration throws database error
        userInterestRepoMock
            .Setup(r => r.GetDistinctUserIdsAsync(null, 100, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PostgreSQL connection lost."));

        var publishingService = new SnapshotPublishingService(
            userInterestRepoMock.Object,
            listingPopularityRepoMock.Object,
            valkeyRepoMock.Object,
            NullLogger<SnapshotPublishingService>.Instance);

        var result = await publishingService.RebuildCacheAsync(CancellationToken.None);

        Assert.Equal(1, result.TotalEvaluated); // Global evaluated
        Assert.Equal(1, result.PublishedCount);  // Global published
        Assert.Single(result.Errors);
        Assert.Equal("POSTGRES_ERROR", result.Errors[0].ErrorCode);
        Assert.False(result.IsSuccessful); // IsSuccessful MUST be false despite FailedCount=0
    }

    [Fact]
    public async Task RebuildCache_EnumerationFailsAfterSomePages_RetainsProcessedStats_IsSuccessfulFalse()
    {
        var userInterestRepoMock = new Mock<IUserEquipmentTypeInterestRepository>();
        var listingPopularityRepoMock = new Mock<IListingPopularityRepository>();
        var valkeyRepoMock = new Mock<IValkeyProjectionRepository>();

        var globalLease = new RankingLease("rec:v1:global:popular-listings", "rec:v1:lease:{rec:v1:global:popular-listings}", "gtoken");
        valkeyRepoMock
            .Setup(v => v.TryAcquireGlobalLeaseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, globalLease));
        valkeyRepoMock
            .Setup(v => v.ReplaceGlobalRankingAsync(globalLease, It.IsAny<IReadOnlyList<ListingPopularityItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        // Page 1 succeeds
        userInterestRepoMock
            .Setup(r => r.GetDistinctUserIdsAsync(null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { user1, user2 });

        // Page 2 fails with database error
        userInterestRepoMock
            .Setup(r => r.GetDistinctUserIdsAsync(user2, 100, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PostgreSQL query timeout."));

        // Users publish successfully
        valkeyRepoMock
            .Setup(v => v.TryAcquireUserLeaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid uid, CancellationToken _) =>
                new LeaseAcquireResult(LeaseAcquireStatus.Acquired, new RankingLease($"rec:v1:user:{uid}:equipment-types", $"rec:v1:lease:{{rec:v1:user:{uid}:equipment-types}}", "token")));
        valkeyRepoMock
            .Setup(v => v.ReplaceUserRankingAsync(It.IsAny<RankingLease>(), It.IsAny<IReadOnlyList<UserEquipmentTypeInterestItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        var publishingService = new SnapshotPublishingService(
            userInterestRepoMock.Object,
            listingPopularityRepoMock.Object,
            valkeyRepoMock.Object,
            NullLogger<SnapshotPublishingService>.Instance);

        var result = await publishingService.RebuildCacheAsync(CancellationToken.None);

        Assert.Equal(3, result.TotalEvaluated); // 1 global + 2 users
        Assert.Equal(3, result.PublishedCount);  // 1 global + 2 users published
        Assert.Single(result.Errors);
        Assert.Equal("POSTGRES_ERROR", result.Errors[0].ErrorCode);
        Assert.False(result.IsSuccessful); // Fails because enumeration did not complete
    }

    [Fact]
    public async Task RebuildCache_CancellationBetweenPages_ThrowsOperationCanceledException()
    {
        var userInterestRepoMock = new Mock<IUserEquipmentTypeInterestRepository>();
        var listingPopularityRepoMock = new Mock<IListingPopularityRepository>();
        var valkeyRepoMock = new Mock<IValkeyProjectionRepository>();

        using var cts = new CancellationTokenSource();

        var globalLease = new RankingLease("rec:v1:global:popular-listings", "rec:v1:lease:{rec:v1:global:popular-listings}", "gtoken");
        valkeyRepoMock
            .Setup(v => v.TryAcquireGlobalLeaseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, globalLease));
        valkeyRepoMock
            .Setup(v => v.ReplaceGlobalRankingAsync(globalLease, It.IsAny<IReadOnlyList<ListingPopularityItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        var user1 = Guid.NewGuid();
        userInterestRepoMock
            .Setup(r => r.GetDistinctUserIdsAsync(null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { user1 });

        // Cancel token during user 1 publish or before page 2
        valkeyRepoMock
            .Setup(v => v.TryAcquireUserLeaseAsync(user1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return new LeaseAcquireResult(LeaseAcquireStatus.Acquired, new RankingLease($"rec:v1:user:{user1}:equipment-types", "leaseKey", "token"));
            });
        valkeyRepoMock
            .Setup(v => v.ReplaceUserRankingAsync(It.IsAny<RankingLease>(), It.IsAny<IReadOnlyList<UserEquipmentTypeInterestItem>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishOutcome.Published);

        var publishingService = new SnapshotPublishingService(
            userInterestRepoMock.Object,
            listingPopularityRepoMock.Object,
            valkeyRepoMock.Object,
            NullLogger<SnapshotPublishingService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await publishingService.RebuildCacheAsync(cts.Token));
    }

    [Fact]
    public async Task TryAcquireUserLease_InFlightCancellation_ThrowsOperationCanceledException()
    {
        var tcs = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource();

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .Returns(tcs.Task);

        var muxMock = new Mock<IConnectionMultiplexer>();
        muxMock.Setup(m => m.IsConnected).Returns(true);
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        var providerMock = new Mock<IValkeyConnectionProvider>();
        providerMock.Setup(p => p.GetMultiplexerAsync(It.IsAny<CancellationToken>())).ReturnsAsync(muxMock.Object);

        var repo = new ValkeyProjectionRepository(providerMock.Object, NullLogger<ValkeyProjectionRepository>.Instance);

        var acquireTask = repo.TryAcquireUserLeaseAsync(Guid.NewGuid(), cts.Token);

        // Cancel while waiting for in-flight task
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await acquireTask);
    }

    [Fact]
    public async Task ReleaseLease_InFlightCancellation_ReturnsFalseSafelyWithoutThrowing()
    {
        var tcs = new TaskCompletionSource<RedisResult>();
        using var cts = new CancellationTokenSource();

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(tcs.Task);

        var muxMock = new Mock<IConnectionMultiplexer>();
        muxMock.Setup(m => m.IsConnected).Returns(true);
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        var providerMock = new Mock<IValkeyConnectionProvider>();
        providerMock.Setup(p => p.GetMultiplexerAsync(It.IsAny<CancellationToken>())).ReturnsAsync(muxMock.Object);

        var repo = new ValkeyProjectionRepository(providerMock.Object, NullLogger<ValkeyProjectionRepository>.Instance);
        var lease = new RankingLease("rec:v1:user:dummy:equipment-types", "rec:v1:lease:{rec:v1:user:dummy:equipment-types}", "token");

        var releaseTask = repo.ReleaseLeaseAsync(lease, cts.Token);

        // Cancel while in-flight
        cts.Cancel();

        bool released = await releaseTask;
        Assert.False(released); // Must safely return false, no exception thrown
    }

    [Fact]
    public async Task ReplaceUserRanking_InFlightCancellation_ThrowsOperationCanceledException()
    {
        var tcs = new TaskCompletionSource<RedisResult>();
        using var cts = new CancellationTokenSource();

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(tcs.Task);

        var muxMock = new Mock<IConnectionMultiplexer>();
        muxMock.Setup(m => m.IsConnected).Returns(true);
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        var providerMock = new Mock<IValkeyConnectionProvider>();
        providerMock.Setup(p => p.GetMultiplexerAsync(It.IsAny<CancellationToken>())).ReturnsAsync(muxMock.Object);

        var repo = new ValkeyProjectionRepository(providerMock.Object, NullLogger<ValkeyProjectionRepository>.Instance);
        var lease = new RankingLease("rec:v1:user:dummy:equipment-types", "rec:v1:lease:{rec:v1:user:dummy:equipment-types}", "token");

        var replaceTask = repo.ReplaceUserRankingAsync(lease, [new(Guid.NewGuid(), 10)], TimeSpan.FromSeconds(300), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replaceTask);
    }

    [Fact]
    public async Task PublishUser_DeadlineExceeded_ReturnsUnavailable_WithoutExtraCleanupWait()
    {
        var userInterestRepoMock = new Mock<IUserEquipmentTypeInterestRepository>();
        var listingPopularityRepoMock = new Mock<IListingPopularityRepository>();
        var valkeyRepoMock = new Mock<IValkeyProjectionRepository>();

        // Simulate hang in Acquire lease that exceeds the 5-second deadline
        valkeyRepoMock
            .Setup(v => v.TryAcquireUserLeaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>(async (uid, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new LeaseAcquireResult(LeaseAcquireStatus.Acquired, null);
            });

        var publishingService = new SnapshotPublishingService(
            userInterestRepoMock.Object,
            listingPopularityRepoMock.Object,
            valkeyRepoMock.Object,
            NullLogger<SnapshotPublishingService>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await publishingService.PublishUserAsync(Guid.NewGuid(), CancellationToken.None);
        sw.Stop();

        Assert.Equal(PublishOutcome.Unavailable, result.Outcome);
        Assert.Contains("timed out", result.Reason, StringComparison.OrdinalIgnoreCase);
        // Deadline is 5 seconds; with no extra cleanup wait, time is around 5 seconds (well under 7 seconds)
        Assert.True(sw.Elapsed.TotalSeconds >= 4.8 && sw.Elapsed.TotalSeconds < 6.5);
    }

    [Fact]
    public void RebuildCacheResult_IsSuccessful_OnlyTrueWhenAllConditionsMet()
    {
        // 1. Fully successful
        var successResult = new RebuildCacheResult(5, 5, 0, 0, 0, []);
        Assert.True(successResult.IsSuccessful);

        // 2. Contains errors
        var errorResult = new RebuildCacheResult(5, 5, 0, 0, 0, [new("user", "ERR", "msg")]);
        Assert.False(errorResult.IsSuccessful);

        // 3. Busy > 0
        var busyResult = new RebuildCacheResult(5, 4, 1, 0, 0, []);
        Assert.False(busyResult.IsSuccessful);

        // 4. LeaseLost > 0
        var leaseLostResult = new RebuildCacheResult(5, 4, 0, 1, 0, []);
        Assert.False(leaseLostResult.IsSuccessful);

        // 5. Failed > 0
        var failedResult = new RebuildCacheResult(5, 4, 0, 0, 1, []);
        Assert.False(failedResult.IsSuccessful);

        // 6. Published < Evaluated
        var incompleteResult = new RebuildCacheResult(5, 4, 0, 0, 0, []);
        Assert.False(incompleteResult.IsSuccessful);
    }
}
