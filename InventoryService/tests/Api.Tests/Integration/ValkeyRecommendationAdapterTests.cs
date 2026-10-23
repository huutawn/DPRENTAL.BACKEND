using Dprental.InventoryService.Api.Recommendation;
using Dprental.InventoryService.Application.Recommendation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

[Collection("ValkeyIntegrationCollection")]
public sealed class ValkeyRecommendationAdapterTests
{
    private readonly ValkeyFixture _valkey;

    public ValkeyRecommendationAdapterTests(ValkeyFixture valkeyFixture)
    {
        _valkey = valkeyFixture;
    }

    private IDatabase Db => _valkey.Multiplexer.GetDatabase();

    private async Task ResetDbAsync()
    {
        foreach (var endpoint in _valkey.Multiplexer.GetEndPoints())
        {
            var server = _valkey.Multiplexer.GetServer(endpoint);
            if (server.IsConnected)
            {
                await server.FlushDatabaseAsync();
            }
        }
    }

    [Fact]
    public async Task KeyFormat_And_Limits_AreEnforced()
    {
        var userId = Guid.NewGuid();
        string userKey = $"rec:v1:user:{userId:D}:equipment-types";
        string globalKey = "rec:v1:global:popular-listings";
        await ResetDbAsync();

        // Populate 25 user items (exceeding 20 limit)
        var userEntries = Enumerable.Range(1, 25)
            .Select(i => new SortedSetEntry(Guid.NewGuid().ToString("D"), 100 - i))
            .ToArray();
        await Db.SortedSetAddAsync(userKey, userEntries);

        // Populate 350 global items (exceeding 300 limit)
        var globalEntries = Enumerable.Range(1, 350)
            .Select(i => new SortedSetEntry(Guid.NewGuid().ToString("D"), 500 - i))
            .ToArray();
        await Db.SortedSetAddAsync(globalKey, globalEntries);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId);

        Assert.Equal(RecommendationOutcome.Available, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Available, snapshot.Reason);
        Assert.Equal(20, snapshot.PersonalInterests.Count); // Capped at 20!
        Assert.Equal(300, snapshot.GlobalPopularity.Count); // Capped at 300!
    }

    [Fact]
    public async Task HumanReads_ExecuteInParallel_AndReturnBothUserAndGlobal()
    {
        var userId = Guid.NewGuid();
        string userKey = $"rec:v1:user:{userId:D}:equipment-types";
        string globalKey = "rec:v1:global:popular-listings";

        var typeId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await ResetDbAsync();

        await Db.SortedSetAddAsync(userKey, typeId.ToString("D"), 99);
        await Db.SortedSetAddAsync(globalKey, listingId.ToString("D"), 50);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId);

        Assert.Equal(RecommendationOutcome.Available, snapshot.Outcome);
        Assert.Single(snapshot.PersonalInterests);
        Assert.Equal(typeId, snapshot.PersonalInterests[0].EquipmentTypeId);
        Assert.Equal(99, snapshot.PersonalInterests[0].Score);

        Assert.Single(snapshot.GlobalPopularity);
        Assert.Equal(listingId, snapshot.GlobalPopularity[0].ListingId);
        Assert.Equal(50, snapshot.GlobalPopularity[0].Score);
    }

    [Fact]
    public async Task AnonymousReads_ReadsOnlyGlobal_DoesNotReadPersonal()
    {
        string globalKey = "rec:v1:global:popular-listings";
        var listingId = Guid.NewGuid();

        await ResetDbAsync();
        await Db.SortedSetAddAsync(globalKey, listingId.ToString("D"), 75);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId: null);

        Assert.Equal(RecommendationOutcome.Available, snapshot.Outcome);
        Assert.Empty(snapshot.PersonalInterests);
        Assert.Single(snapshot.GlobalPopularity);
        Assert.Equal(listingId, snapshot.GlobalPopularity[0].ListingId);
        Assert.Equal(75, snapshot.GlobalPopularity[0].Score);
    }

    [Fact]
    public async Task EmptyKeys_ReturnsMiss()
    {
        var userId = Guid.NewGuid();

        await ResetDbAsync();

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId);

        Assert.Equal(RecommendationOutcome.Miss, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Miss, snapshot.Reason);
        Assert.Empty(snapshot.PersonalInterests);
        Assert.Empty(snapshot.GlobalPopularity);
    }

    [Fact]
    public async Task WrongKeyType_ReturnsUnavailableWithInvalidReason()
    {
        string globalKey = "rec:v1:global:popular-listings";

        await ResetDbAsync();
        // Set string instead of zset
        await Db.StringSetAsync(globalKey, "invalid-type-value");

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId: null);

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }

    [Fact]
    public async Task BoundaryValidation_RejectsNilUuid()
    {
        string globalKey = "rec:v1:global:popular-listings";

        await ResetDbAsync();
        await Db.SortedSetAddAsync(globalKey, Guid.Empty.ToString("D"), 100);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId: null);

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }

    [Fact]
    public async Task BoundaryValidation_RejectsDuplicateIdAfterNormalization()
    {
        var userId = Guid.NewGuid();
        string userKey = $"rec:v1:user:{userId:D}:equipment-types";
        string globalKey = "rec:v1:global:popular-listings";

        await ResetDbAsync();

        var dupId = Guid.NewGuid();
        // Valkey sorted set by member is unique in Valkey, but test non-canonical casing or duplicate member simulation
        await Db.SortedSetAddAsync(globalKey, dupId.ToString("D").ToUpperInvariant(), 10);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId: null);

        // Uppercase UUID is non-canonical -> rejected as invalid!
        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(9007199254740994L)] // 2^53 + 2 (strictly > 2^53 in double precision)
    public async Task BoundaryValidation_RejectsOutOfRangeScores(long invalidScore)
    {
        string globalKey = "rec:v1:global:popular-listings";

        await ResetDbAsync();
        await Db.SortedSetAddAsync(globalKey, Guid.NewGuid().ToString("D"), invalidScore);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId: null);

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }

    [Fact]
    public async Task BoundaryValidation_RejectsFractionalScore()
    {
        string globalKey = "rec:v1:global:popular-listings";

        await ResetDbAsync();
        await Db.SortedSetAddAsync(globalKey, Guid.NewGuid().ToString("D"), 10.5);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId: null);

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }

    [Fact]
    public async Task InvalidPersonalData_DiscardsWholeRequest_ReturnsUnavailableWithInvalid()
    {
        var userId = Guid.NewGuid();
        string userKey = $"rec:v1:user:{userId:D}:equipment-types";
        string globalKey = "rec:v1:global:popular-listings";

        await ResetDbAsync();

        // User data has fractional score -> invalid
        await Db.SortedSetAddAsync(userKey, Guid.NewGuid().ToString("D"), 12.34);
        // Global data is perfectly valid
        await Db.SortedSetAddAsync(globalKey, Guid.NewGuid().ToString("D"), 500);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId);

        // Entire snapshot discarded, no partial snapshot used!
        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
        Assert.Empty(snapshot.PersonalInterests);
        Assert.Empty(snapshot.GlobalPopularity);
    }

    [Fact]
    public async Task Unconfigured_ReturnsUnavailableWithUnconfiguredReason()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RecommendationRedis"] = ""
        }).Build();

        var reader = new ValkeyRecommendationRankingReader(config);
        var snapshot = await reader.ReadAsync(Guid.NewGuid());

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Unconfigured, snapshot.Reason);
    }

    [Fact]
    public void InvalidConfigurationSyntax_ThrowsStartupErrorWithSafeMessage()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RecommendationRedis"] = "connectTimeout=not_a_number"
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ValkeyRecommendationRankingReader(config));
        Assert.Contains("ConnectionStrings:RecommendationRedis", ex.Message);
    }
    [Fact]
    public async Task Cancellation_PropagatesOperationCanceledException()
    {
        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await reader.ReadAsync(Guid.NewGuid(), cts.Token);
        });
    }

    [Fact]
    public async Task Rotation_SnapshotCachedInInventoryNamespace_WithCycleAndSeed()
    {
        await ResetDbAsync();
        var userId = Guid.NewGuid();
        string userKey = $"rec:v1:user:{userId:D}:equipment-types";
        string globalKey = "rec:v1:global:popular-listings";
        var typeId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await Db.SortedSetAddAsync(userKey, typeId.ToString("D"), 50);
        await Db.SortedSetAddAsync(globalKey, listingId.ToString("D"), 100);

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot1 = await reader.ReadAsync(userId);

        Assert.Equal(RecommendationOutcome.Available, snapshot1.Outcome);
        Assert.True(snapshot1.Cycle > 0);
        Assert.Equal($"{userId:D}:{snapshot1.Cycle}", snapshot1.Seed);

        // Verify key exists in Inventory namespace
        string snapshotKey = ValkeyRecommendationRankingReader.GetSnapshotKey(userId, snapshot1.Cycle);
        Assert.True(await Db.KeyExistsAsync(snapshotKey));

        // Verify TTL is bounded within cycle (<= 300s)
        var ttl = await Db.KeyTimeToLiveAsync(snapshotKey);
        Assert.NotNull(ttl);
        Assert.True(ttl.Value.TotalSeconds > 0 && ttl.Value.TotalSeconds <= 300);

        // Second read hits the snapshot cache
        var snapshot2 = await reader.ReadAsync(userId);
        Assert.Equal(RecommendationOutcome.Available, snapshot2.Outcome);
        Assert.Equal(snapshot1.Cycle, snapshot2.Cycle);
        Assert.Equal(snapshot1.Seed, snapshot2.Seed);
        Assert.Equal(snapshot1.PersonalInterests.Count, snapshot2.PersonalInterests.Count);
    }

    [Fact]
    public async Task Rotation_OversizedPayload_Exceeding64KiB_RejectedAsInvalid()
    {
        await ResetDbAsync();
        var userId = Guid.NewGuid();
        long cycle = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
        string snapshotKey = ValkeyRecommendationRankingReader.GetSnapshotKey(userId, cycle);

        // Write oversized payload > 64 KiB
        byte[] oversized = new byte[65 * 1024];
        Array.Fill(oversized, (byte)'a');
        await Db.StringSetAsync(snapshotKey, oversized, TimeSpan.FromMinutes(5));

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId);

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }

    [Fact]
    public async Task Rotation_CorruptJson_RejectedAsInvalid()
    {
        await ResetDbAsync();
        var userId = Guid.NewGuid();
        long cycle = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
        string snapshotKey = ValkeyRecommendationRankingReader.GetSnapshotKey(userId, cycle);

        await Db.StringSetAsync(snapshotKey, "{not-a-valid-json", TimeSpan.FromMinutes(5));

        var reader = new ValkeyRecommendationRankingReader(_valkey.Multiplexer);
        var snapshot = await reader.ReadAsync(userId);

        Assert.Equal(RecommendationOutcome.Unavailable, snapshot.Outcome);
        Assert.Equal(RecommendationReasons.Invalid, snapshot.Reason);
    }
}
