using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.InventoryService.Api.Recommendation;
using Dprental.InventoryService.Application.Recommendation;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

[Collection("ValkeyIntegrationCollection")]
public sealed class RecommendationSnapshotConsensusTests(ValkeyFixture valkey)
{
    private IDatabase Db => valkey.Multiplexer.GetDatabase();
    private static long Cycle(DateTimeOffset now) => now.ToUnixTimeSeconds() / 300;
    private static Task<RedisResult> LostNx() => Task.FromResult(RedisResult.Create(RedisValue.Null));

    private ValkeyRecommendationRankingReader Reader(IDatabase db, TimeProvider? clock = null) =>
        new(RedisCallProxy.WithDatabase(valkey.Multiplexer, db), timeProvider: clock);

    [Fact]
    public async Task ConcurrentReaders_WithDifferentSources_ReturnTheSameWinner()
    {
        await valkey.FlushDbAsync();
        var bothSources = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sourceReads = 0;
        IDatabase Intercept(Guid listing) => RedisCallProxy.Wrap(Db, (method, _, next) =>
        {
            if (method.Name == "SortedSetRangeByRankWithScoresAsync")
            {
                if (Interlocked.Increment(ref sourceReads) == 2) bothSources.SetResult();
                return Task.FromResult(new[] { new SortedSetEntry(listing.ToString("D"), 10) });
            }
            if (method.Name == "ExecuteAsync") return WriteAfterBothSourcesAsync(next);
            return next();
        });
        async Task<RedisResult> WriteAfterBothSourcesAsync(Func<object?> next)
        {
            await bothSources.Task;
            return await (Task<RedisResult>)next()!;
        }
        await using var first = Reader(Intercept(Guid.NewGuid()));
        await using var second = Reader(Intercept(Guid.NewGuid()));
        var snapshots = await Task.WhenAll(first.ReadAsync(null), second.ReadAsync(null));
        Assert.All(snapshots, s => Assert.Equal(RecommendationOutcome.Available, s.Outcome));
        Assert.Equal(snapshots[0].GlobalPopularity, snapshots[1].GlobalPopularity);
    }

    [Theory]
    [InlineData("missing", RecommendationOutcome.Miss, RecommendationReasons.Miss)]
    [InlineData("invalid", RecommendationOutcome.Unavailable, RecommendationReasons.Invalid)]
    [InlineData("wrongtype", RecommendationOutcome.Unavailable, RecommendationReasons.Invalid)]
    [InlineData("timeout", RecommendationOutcome.Unavailable, RecommendationReasons.Timeout)]
    [InlineData("connection", RecommendationOutcome.Unavailable, RecommendationReasons.Unavailable)]
    [InlineData("redis-timeout", RecommendationOutcome.Unavailable, RecommendationReasons.Timeout)]
    [InlineData("write-denied", RecommendationOutcome.Unavailable, RecommendationReasons.Unavailable)]
    public async Task FailedConsensus_NeverServesLocalScores(string failure, RecommendationOutcome outcome, string reason)
    {
        await valkey.FlushDbAsync();
        await Db.SortedSetAddAsync("rec:v1:global:popular-listings", Guid.NewGuid().ToString("D"), 10);
        int gets = 0;
        var pending = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var db = RedisCallProxy.Wrap(Db, (method, _, next) =>
        {
            if (method.Name == "ExecuteAsync")
                return failure == "write-denied"
                    ? Task.FromException<RedisResult>(new RedisServerException(default, CommandFlags.None, "NOPERM snapshot write denied")) : LostNx();
            if (method.Name == "StringGetAsync" && Interlocked.Increment(ref gets) == 2)
                return failure switch
                {
                    "missing" => Task.FromResult(RedisValue.Null),
                    "invalid" => Task.FromResult((RedisValue)"{}"),
                    "wrongtype" => Task.FromException<RedisValue>(new RedisServerException(default, CommandFlags.None, "WRONGTYPE snapshot")),
                    "timeout" => pending.Task,
                    "redis-timeout" => Task.FromException<RedisValue>(new RedisTimeoutException(CommandFlags.None, "test timeout", default)),
                    _ => Task.FromException<RedisValue>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "test outage", null, default))
                };
            return next();
        });
        await using var reader = Reader(db);
        var result = await reader.ReadAsync(null);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(result.GlobalPopularity);
        pending.TrySetException(new RedisException("Late failure must be observed"));
    }

    [Fact]
    public async Task SnapshotUsesAbsoluteExpiry_AndPinsScoresUntilNextCycle()
    {
        await valkey.FlushDbAsync();
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableRecommendationClock(now);
        string sourceKey = "rec:v1:global:popular-listings";
        var listing = Guid.NewGuid();
        await Db.SortedSetAddAsync(sourceKey, listing.ToString("D"), 10);
        await using var reader = Reader(Db, clock);
        var first = await reader.ReadAsync(null);
        Assert.Equal(RecommendationOutcome.Available, first.Outcome);
        string key = ValkeyRecommendationRankingReader.GetSnapshotKey(null, Cycle(now));
        long expectedExpiry = (Cycle(now) + 1) * 300000L;
        Assert.Equal(expectedExpiry, (long)await Db.ExecuteAsync("PEXPIRETIME", key));
        await Db.SortedSetAddAsync(sourceKey, listing.ToString("D"), 99);
        var second = await reader.ReadAsync(null);
        Assert.Equal(10, Assert.Single(second.GlobalPopularity).Score);
        Assert.Equal(expectedExpiry, (long)await Db.ExecuteAsync("PEXPIRETIME", key));
        clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(expectedExpiry);
        var next = await reader.ReadAsync(null);
        Assert.Equal(99, Assert.Single(next.GlobalPopularity).Score);
        Assert.NotEqual(first.Seed, next.Seed);
    }

    [Fact]
    public async Task CycleEndingDuringWrite_ReturnsTimeout()
    {
        await valkey.FlushDbAsync();
        var clock = new MutableRecommendationClock(DateTimeOffset.UtcNow);
        long end = (Cycle(clock.Now) + 1) * 300000L;
        await Db.SortedSetAddAsync("rec:v1:global:popular-listings", Guid.NewGuid().ToString("D"), 10);
        var db = RedisCallProxy.Wrap(Db, (method, _, next) =>
        {
            if (method.Name != "ExecuteAsync") return next();
            clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(end);
            return next();
        });
        await using var reader = Reader(db, clock);
        var result = await reader.ReadAsync(null);
        Assert.Equal(RecommendationReasons.Timeout, result.Reason);
    }

    [Fact]
    public async Task ClientCancellationDuringWinnerRead_Propagates()
    {
        await valkey.FlushDbAsync();
        await Db.SortedSetAddAsync("rec:v1:global:popular-listings", Guid.NewGuid().ToString("D"), 10);
        using var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        int gets = 0;
        var db = RedisCallProxy.Wrap(Db, (method, _, next) =>
        {
            if (method.Name == "ExecuteAsync") return LostNx();
            if (method.Name == "StringGetAsync" && Interlocked.Increment(ref gets) == 2)
            {
                cts.Cancel();
                return pending.Task;
            }
            return next();
        });
        await using var reader = Reader(db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(null, cts.Token));
        pending.SetResult(RedisValue.Null);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("cycle")]
    [InlineData("seed")]
    [InlineData("personal")]
    [InlineData("global")]
    [InlineData("empty")]
    [InlineData("anonymous-personal")]
    [InlineData("duplicate")]
    [InlineData("score")]
    public async Task InvalidSnapshotIsRejected(string defect)
    {
        await valkey.FlushDbAsync();
        long cycle = Cycle(DateTimeOffset.UtcNow);
        var listing = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToNode(new
        {
            version = 1,
            cycle,
            seed = $"anon:{cycle}",
            personal = Array.Empty<object>(),
            global = new[] { new { listingId = listing, score = 10L } }
        })!;
        if (defect == "empty") payload["global"] = new JsonArray();
        else if (defect == "anonymous-personal") payload["personal"] = JsonSerializer.SerializeToNode(new[] { new { typeId = Guid.NewGuid(), score = 1 } });
        else if (defect == "duplicate") ((JsonArray)payload["global"]!).Add(payload["global"]![0]!.DeepClone());
        else if (defect == "score") payload["global"]![0]!["score"] = 0;
        else ((JsonObject)payload).Remove(defect);
        await Db.StringSetAsync(ValkeyRecommendationRankingReader.GetSnapshotKey(null, cycle), payload.ToJsonString());
        await using var reader = Reader(Db);
        var result = await reader.ReadAsync(null);
        Assert.Equal(RecommendationReasons.Invalid, result.Reason);
    }
}
