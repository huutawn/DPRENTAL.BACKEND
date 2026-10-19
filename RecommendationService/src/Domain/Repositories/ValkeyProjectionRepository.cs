using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class ValkeyProjectionRepository : IValkeyProjectionRepository
{
    private const string SetSnapshotLuaScript = @"
        local existing = redis.call('GET', KEYS[1])
        if existing then
            local ok, decoded = pcall(cjson.decode, existing)
            if ok and decoded and decoded.generatedAt then
                if ARGV[2] < decoded.generatedAt then
                    return 0
                end
            end
        end
        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[3])
        return 1
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

    public async Task<bool> SetUserEquipmentTypesSnapshotAsync(
        Guid userId,
        string jsonSnapshot,
        string generatedAtIso,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key = $"rec:v1:user:{userId:D}:equipment-types";
        return await SetSnapshotInternalAsync(key, jsonSnapshot, generatedAtIso, ttl, cancellationToken);
    }

    public async Task<bool> SetGlobalPopularListingsSnapshotAsync(
        string jsonSnapshot,
        string generatedAtIso,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string key = "rec:v1:global:popular-listings";
        return await SetSnapshotInternalAsync(key, jsonSnapshot, generatedAtIso, ttl, cancellationToken);
    }

    public async Task<string?> GetUserEquipmentTypesSnapshotAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key = $"rec:v1:user:{userId:D}:equipment-types";
        return await GetSnapshotInternalAsync(key, cancellationToken);
    }

    public async Task<string?> GetGlobalPopularListingsSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string key = "rec:v1:global:popular-listings";
        return await GetSnapshotInternalAsync(key, cancellationToken);
    }

    private async Task<bool> SetSnapshotInternalAsync(
        string key,
        string jsonSnapshot,
        string generatedAtIso,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redis = await _connectionProvider.GetMultiplexerAsync(cancellationToken);
        if (redis == null || !redis.IsConnected)
        {
            _logger.LogWarning("Valkey is not connected. Skipping snapshot write for key {Key}.", key);
            return false;
        }

        try
        {
            var db = redis.GetDatabase();
            var result = await db.ScriptEvaluateAsync(
                SetSnapshotLuaScript,
                keys: [new RedisKey(key)],
                values: [
                    (RedisValue)jsonSnapshot,
                    (RedisValue)generatedAtIso,
                    (RedisValue)(long)Math.Max(1, ttl.TotalSeconds)
                ]);

            return (int)result == 1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to write snapshot to Valkey for key {Key}.", key);
            return false;
        }
    }

    private async Task<string?> GetSnapshotInternalAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var redis = await _connectionProvider.GetMultiplexerAsync(cancellationToken);
        if (redis == null || !redis.IsConnected)
        {
            return null;
        }

        try
        {
            var db = redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to read snapshot from Valkey for key {Key}.", key);
            return null;
        }
    }
}
