using System.Globalization;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class ValkeyProjectionRepository : IValkeyProjectionRepository
{
    private const long MaxSafeScore = 9007199254740992L; // 2^53 integer precision limit for ZSET
    private const int MaxUserInterests = 20;
    private const int MaxGlobalPopularListings = 300;
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMilliseconds(30000);

    private const string ReplaceRankingLuaScript = @"
        local currentLease = redis.call('GET', KEYS[2])
        if not currentLease or currentLease ~= ARGV[1] then
            return -1
        end

        local numArgs = #ARGV - 2
        if numArgs % 2 ~= 0 then
            return -2
        end

        redis.call('DEL', KEYS[1])

        if numArgs > 0 then
            local zaddArgs = { KEYS[1] }
            for i = 3, #ARGV do
                table.insert(zaddArgs, ARGV[i])
            end
            local unpackFn = table.unpack or unpack
            redis.call('ZADD', unpackFn(zaddArgs))

            local ttl = tonumber(ARGV[2])
            if ttl and ttl > 0 then
                redis.call('EXPIRE', KEYS[1], ttl)
            end
        end

        redis.call('DEL', KEYS[2])
        return 1
    ";

    private const string CompareAndDeleteLuaScript = @"
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
    ";

    private readonly IValkeyConnectionProvider _connectionProvider;
    private readonly ILogger<ValkeyProjectionRepository> _logger;

    public ValkeyProjectionRepository(
        IValkeyConnectionProvider connectionProvider,
        ILogger<ValkeyProjectionRepository> logger)
    {
        _connectionProvider = connectionProvider;
        _logger = logger;
    }

    public static string GetUserDataKey(Guid userId) =>
        $"rec:v1:user:{userId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant()}:equipment-types";

    public static string GetUserLeaseKey(Guid userId) =>
        $"rec:v1:lease:{{{GetUserDataKey(userId)}}}";

    public const string GlobalDataKey = "rec:v1:global:popular-listings";
    public const string GlobalLeaseKey = $"rec:v1:lease:{{{GlobalDataKey}}}";

    public Task<LeaseAcquireResult> TryAcquireUserLeaseAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string dataKey = GetUserDataKey(userId);
        string leaseKey = GetUserLeaseKey(userId);
        return TryAcquireLeaseInternalAsync(dataKey, leaseKey, cancellationToken);
    }

    public Task<LeaseAcquireResult> TryAcquireGlobalLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryAcquireLeaseInternalAsync(GlobalDataKey, GlobalLeaseKey, cancellationToken);
    }

    public async Task<PublishOutcome> ReplaceUserRankingAsync(
        RankingLease lease,
        IReadOnlyList<UserEquipmentTypeInterestItem> items,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (items.Count > MaxUserInterests)
        {
            _logger.LogError("User ranking items count {Count} exceeds maximum allowed limit {Max}.", items.Count, MaxUserInterests);
            return PublishOutcome.Failed;
        }

        var seenIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item.EquipmentTypeId == Guid.Empty)
            {
                _logger.LogError("User ranking item contains empty (nil) UUID.");
                return PublishOutcome.Failed;
            }

            if (!seenIds.Add(item.EquipmentTypeId))
            {
                _logger.LogError("User ranking items contain duplicate equipmentTypeId: {Id}", item.EquipmentTypeId);
                return PublishOutcome.Failed;
            }

            if (item.Score < 1L || item.Score > MaxSafeScore)
            {
                _logger.LogError("User ranking score {Score} is out of safe range [1, {MaxSafeScore}].", item.Score, MaxSafeScore);
                return PublishOutcome.Failed;
            }
        }

        var zaddPairs = new List<(long Score, string Member)>(items.Count);
        foreach (var item in items)
        {
            zaddPairs.Add((item.Score, item.EquipmentTypeId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant()));
        }

        return await ExecuteReplaceRankingScriptAsync(lease, zaddPairs, ttl, cancellationToken);
    }

    public async Task<PublishOutcome> ReplaceGlobalRankingAsync(
        RankingLease lease,
        IReadOnlyList<ListingPopularityItem> items,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (items.Count > MaxGlobalPopularListings)
        {
            _logger.LogError("Global ranking items count {Count} exceeds maximum allowed limit {Max}.", items.Count, MaxGlobalPopularListings);
            return PublishOutcome.Failed;
        }

        var seenIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item.EquipmentListingId == Guid.Empty)
            {
                _logger.LogError("Global ranking item contains empty (nil) UUID.");
                return PublishOutcome.Failed;
            }

            if (!seenIds.Add(item.EquipmentListingId))
            {
                _logger.LogError("Global ranking items contain duplicate equipmentListingId: {Id}", item.EquipmentListingId);
                return PublishOutcome.Failed;
            }

            if (item.Score < 1L || item.Score > MaxSafeScore)
            {
                _logger.LogError("Global ranking score {Score} is out of safe range [1, {MaxSafeScore}].", item.Score, MaxSafeScore);
                return PublishOutcome.Failed;
            }
        }

        var zaddPairs = new List<(long Score, string Member)>(items.Count);
        foreach (var item in items)
        {
            zaddPairs.Add((item.Score, item.EquipmentListingId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant()));
        }

        return await ExecuteReplaceRankingScriptAsync(lease, zaddPairs, ttl, cancellationToken);
    }

    public async Task<bool> ReleaseLeaseAsync(RankingLease lease, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            var muxTask = _connectionProvider.GetMultiplexerAsync(cancellationToken);
            var mux = await muxTask.WaitAsync(cancellationToken);
            if (mux == null || !mux.IsConnected)
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var db = mux.GetDatabase();
            RedisKey[] keys = [lease.LeaseKey];
            RedisValue[] values = [lease.Token];

            var evalTask = db.ScriptEvaluateAsync(CompareAndDeleteLuaScript, keys, values);
            var result = await evalTask.WaitAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return (int)result == 1;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release lease {LeaseKey} cleanly.", lease.LeaseKey);
            return false;
        }
    }

    private async Task<LeaseAcquireResult> TryAcquireLeaseInternalAsync(string dataKey, string leaseKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var muxTask = _connectionProvider.GetMultiplexerAsync(cancellationToken);
            var mux = await muxTask.WaitAsync(cancellationToken);
            if (mux == null || !mux.IsConnected)
            {
                return new LeaseAcquireResult(LeaseAcquireStatus.Unavailable, Reason: "Valkey is unavailable or connection is not established.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var db = mux.GetDatabase();
            string token = Guid.NewGuid().ToString("N");

            var setTask = db.StringSetAsync(leaseKey, token, LeaseTtl, When.NotExists);
            bool acquired = await setTask.WaitAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (acquired)
            {
                return new LeaseAcquireResult(LeaseAcquireStatus.Acquired, new RankingLease(dataKey, leaseKey, token));
            }

            return new LeaseAcquireResult(LeaseAcquireStatus.Busy, Reason: "Ranking is currently leased by another writer.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException or TimeoutException)
        {
            _logger.LogWarning(ex, "Valkey connection timeout/error while acquiring lease for {Key}.", dataKey);
            return new LeaseAcquireResult(LeaseAcquireStatus.Unavailable, Reason: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error acquiring lease for {Key}.", dataKey);
            return new LeaseAcquireResult(LeaseAcquireStatus.Failed, Reason: ex.Message);
        }
    }

    private async Task<PublishOutcome> ExecuteReplaceRankingScriptAsync(
        RankingLease lease,
        List<(long Score, string Member)> items,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var muxTask = _connectionProvider.GetMultiplexerAsync(cancellationToken);
            var mux = await muxTask.WaitAsync(cancellationToken);
            if (mux == null || !mux.IsConnected)
            {
                return PublishOutcome.Unavailable;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var db = mux.GetDatabase();
            RedisKey[] keys = [lease.DataKey, lease.LeaseKey];

            int argCount = 2 + (items.Count * 2);
            var args = new RedisValue[argCount];
            args[0] = lease.Token;
            args[1] = (int)ttl.TotalSeconds;

            int argIdx = 2;
            foreach (var (score, member) in items)
            {
                args[argIdx++] = score.ToString(CultureInfo.InvariantCulture);
                args[argIdx++] = member;
            }

            var evalTask = db.ScriptEvaluateAsync(ReplaceRankingLuaScript, keys, args);
            var result = await evalTask.WaitAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            int code = (int)result;

            return code switch
            {
                1 => PublishOutcome.Published,
                -1 => PublishOutcome.LeaseLost,
                _ => PublishOutcome.Failed
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException or TimeoutException)
        {
            _logger.LogWarning(ex, "Valkey connection timeout/error while replacing ranking for {Key}.", lease.DataKey);
            return PublishOutcome.Unavailable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing ranking replacement Lua for {Key}.", lease.DataKey);
            return PublishOutcome.Failed;
        }
    }
}
