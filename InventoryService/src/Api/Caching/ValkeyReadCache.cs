using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Caching;

/// <summary>
/// Triển khai IInventoryReadCache sử dụng Valkey/Redis với StackExchange.Redis (v2).
/// Hỗ trợ đọc bulk, ghi có điều kiện theo generation token, hash tags theo slot,
/// gate-based coalescing theo key, giới hạn 32 fills / 1024 waiters, deadline tuyệt đối PXAT,
/// và generation TTL dẫn xuất.
/// </summary>
public sealed class ValkeyReadCache : IInventoryReadCache, IDisposable
{
    private const int CurrentSchemaVersion = 2;

    private const string GetOrInitGenLuaScript = @"
local gen = redis.call('GET', KEYS[1])
local genTtlMs = tonumber(ARGV[1])
if not gen then
    gen = ARGV[2]
    redis.call('SET', KEYS[1], gen, 'PX', genTtlMs)
    return { gen, false }
else
    local currentPttl = redis.call('PTTL', KEYS[1])
    if currentPttl < genTtlMs then
        redis.call('PEXPIRE', KEYS[1], genTtlMs)
    end
end
local val = redis.call('GET', KEYS[2])
return { gen, val or false }";

    private const string ConditionalSetLuaScript = @"
local currentGen = redis.call('GET', KEYS[1])
if currentGen == ARGV[1] then
    local pxat = tonumber(ARGV[2])
    local time = redis.call('TIME')
    local nowMs = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
    if pxat > nowMs then
        redis.call('SET', KEYS[2], ARGV[3], 'PXAT', pxat)
        local genPttl = redis.call('PTTL', KEYS[1])
        local requiredGenPttl = (pxat - nowMs) + 60000
        if genPttl < requiredGenPttl then
            redis.call('PEXPIRE', KEYS[1], requiredGenPttl)
        end
        return 1
    end
end
return 0";

    private const string InvalidateLuaScript = @"
local newGen = ARGV[1]
local genTtlMs = tonumber(ARGV[2])
redis.call('SET', KEYS[1], newGen, 'PX', genTtlMs)
return 1";

    private const string GetBulkLuaScript = @"
local gen = redis.call('GET', KEYS[1])
local genTtlMs = tonumber(ARGV[1])
if not gen then
    gen = ARGV[2]
    redis.call('SET', KEYS[1], gen, 'PX', genTtlMs)
    local emptyRes = { gen }
    for i = 2, #KEYS do
        table.insert(emptyRes, false)
    end
    return emptyRes
else
    local currentPttl = redis.call('PTTL', KEYS[1])
    if currentPttl < genTtlMs then
        redis.call('PEXPIRE', KEYS[1], genTtlMs)
    end
end
local res = { gen }
for i = 2, #KEYS do
    table.insert(res, redis.call('GET', KEYS[i]))
end
return res";

    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly InventoryCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly InventoryCacheMetrics? _metrics;
    private readonly ILogger<ValkeyReadCache>? _logger;

    private readonly SemaphoreSlim _sourceFillSemaphore;
    private readonly ConcurrentDictionary<string, KeyGate> _keyGates = new(StringComparer.Ordinal);
    private int _waitingFillCount;
    private const int MaxWaitingFillRequests = 1024;

    private static readonly AsyncLocal<bool> HasFillPermitHolder = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsEnabled => _options.Enabled && _multiplexer != null && _multiplexer.IsConnected;

    public ValkeyReadCache(
        IOptions<InventoryCacheOptions> options,
        IConnectionMultiplexer? multiplexer = null,
        TimeProvider? timeProvider = null,
        InventoryCacheMetrics? metrics = null,
        ILogger<ValkeyReadCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? new InventoryCacheOptions();
        _multiplexer = multiplexer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
        _logger = logger;
        _sourceFillSemaphore = new SemaphoreSlim(_options.MaxConcurrentFills, _options.MaxConcurrentFills);
    }

    private int GetDefaultGenerationTtlMs()
    {
        int maxTtl = Math.Max(_options.DynamicTtlSeconds, Math.Max(_options.MetadataTtlSeconds, _options.CatalogTtlSeconds));
        return (maxTtl + 60) * 1000;
    }

    /// <inheritdoc />
    public async Task<CacheReadResult<T>> GetAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryType);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHash);

        var scopeType = GetScopeType(scope);
        if (!IsEnabled || _multiplexer == null)
        {
            _metrics?.RecordBypass(scopeType, queryType);
            return new CacheReadResult<T>(default, string.Empty, false);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var db = _multiplexer.GetDatabase();
            var (genKey, entryKey) = GetKeys(scope, queryType, inputHash);
            var candidateGen = Guid.NewGuid().ToString("N");
            int genTtlMs = GetDefaultGenerationTtlMs();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            var redisResult = (RedisResult[]?)await db.ScriptEvaluateAsync(
                GetOrInitGenLuaScript,
                new[] { genKey, entryKey },
                new RedisValue[] { genTtlMs, candidateGen }).WaitAsync(timeoutCts.Token);

            sw.Stop();

            if (redisResult == null || redisResult.Length < 2)
            {
                _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                return new CacheReadResult<T>(default, candidateGen, false);
            }

            var currentGen = (string)redisResult[0]!;
            var rawValue = redisResult[1];

            if (rawValue.IsNull)
            {
                _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                return new CacheReadResult<T>(default, currentGen, false);
            }

            var jsonString = (string)rawValue!;
            var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(jsonString, SerializerOptions);

            var nowUtc = _timeProvider.GetUtcNow();
            if (envelope == null ||
                envelope.SchemaVersion != CurrentSchemaVersion ||
                !string.Equals(envelope.GenerationToken, currentGen, StringComparison.Ordinal) ||
                envelope.ExpiresAtUtc <= nowUtc)
            {
                _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                return new CacheReadResult<T>(default, currentGen, false);
            }

            _metrics?.RecordHit(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
            return new CacheReadResult<T>(envelope.Data, currentGen, true, envelope.ExpiresAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics?.RecordError(scopeType, queryType, "get");
            _logger?.LogWarning(ex, "Lỗi đọc Valkey cache cho scope {Scope}, query {QueryType}. Fallback miss.", scope, queryType);
            return new CacheReadResult<T>(default, string.Empty, false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        T value,
        string expectedGenerationToken,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryType);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHash);

        var scopeType = GetScopeType(scope);
        if (!IsEnabled || _multiplexer == null || string.IsNullOrWhiteSpace(expectedGenerationToken))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (expiresAtUtc <= now)
        {
            return false;
        }

        try
        {
            var envelope = new CacheEnvelope<T>(CurrentSchemaVersion, expectedGenerationToken, expiresAtUtc, value);
            var json = JsonSerializer.Serialize(envelope, SerializerOptions);

            var byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > _options.MaxPayloadBytes)
            {
                _logger?.LogWarning("Payload cache cho {Scope}:{QueryType} vượt quá giới hạn {MaxBytes} bytes ({ActualBytes} bytes). Bỏ qua ghi cache.",
                    scope, queryType, _options.MaxPayloadBytes, byteCount);
                return false;
            }

            var db = _multiplexer.GetDatabase();
            var (genKey, entryKey) = GetKeys(scope, queryType, inputHash);
            long pxatMs = expiresAtUtc.ToUnixTimeMilliseconds();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            var result = (int)await db.ScriptEvaluateAsync(
                ConditionalSetLuaScript,
                new[] { genKey, entryKey },
                new RedisValue[] { expectedGenerationToken, pxatMs, json }).WaitAsync(timeoutCts.Token);

            if (result == 1)
            {
                return true;
            }

            _metrics?.RecordGenerationRejection(scopeType, queryType);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError(scopeType, queryType, "set");
            _logger?.LogWarning(ex, "Lỗi ghi Valkey cache cho scope {Scope}, query {QueryType}.", scope, queryType);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(
        string scope,
        string queryType,
        IReadOnlyList<string> inputHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryType);
        ArgumentNullException.ThrowIfNull(inputHashes);

        var result = inputHashes.ToDictionary(h => h, h => new CacheReadResult<T>(default, string.Empty, false));
        if (inputHashes.Count == 0)
        {
            return result;
        }

        var scopeType = GetScopeType(scope);
        if (!IsEnabled || _multiplexer == null)
        {
            _metrics?.RecordBypass(scopeType, queryType);
            return result;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var db = _multiplexer.GetDatabase();
            var genKey = GetGenKey(scope);
            var entryKeys = inputHashes.Select(h => GetEntryKey(scope, queryType, h)).ToArray();

            var keys = new RedisKey[entryKeys.Length + 1];
            keys[0] = genKey;
            Array.Copy(entryKeys, 0, keys, 1, entryKeys.Length);

            var candidateGen = Guid.NewGuid().ToString("N");
            int genTtlMs = GetDefaultGenerationTtlMs();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            var redisResult = (RedisResult[]?)await db.ScriptEvaluateAsync(
                GetBulkLuaScript,
                keys,
                new RedisValue[] { genTtlMs, candidateGen }).WaitAsync(timeoutCts.Token);

            sw.Stop();

            if (redisResult == null || redisResult.Length < 1)
            {
                _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                return result;
            }

            var currentGen = (string)redisResult[0]!;
            var nowUtc = _timeProvider.GetUtcNow();

            for (int i = 0; i < inputHashes.Count; i++)
            {
                var hash = inputHashes[i];
                var raw = redisResult[i + 1];

                if (raw.IsNull)
                {
                    result[hash] = new CacheReadResult<T>(default, currentGen, false);
                    _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                    continue;
                }

                try
                {
                    var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>((string)raw!, SerializerOptions);
                    if (envelope != null &&
                        envelope.SchemaVersion == CurrentSchemaVersion &&
                        string.Equals(envelope.GenerationToken, currentGen, StringComparison.Ordinal) &&
                        envelope.ExpiresAtUtc > nowUtc)
                    {
                        result[hash] = new CacheReadResult<T>(envelope.Data, currentGen, true, envelope.ExpiresAtUtc);
                        _metrics?.RecordHit(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                    }
                    else
                    {
                        result[hash] = new CacheReadResult<T>(default, currentGen, false);
                        _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                    }
                }
                catch
                {
                    result[hash] = new CacheReadResult<T>(default, currentGen, false);
                    _metrics?.RecordMiss(scopeType, queryType, sw.Elapsed.TotalMilliseconds);
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics?.RecordError(scopeType, queryType, "get-bulk");
            _logger?.LogWarning(ex, "Lỗi đọc bulk Valkey cache cho scope {Scope}, query {QueryType}. Fallback miss.", scope, queryType);
            return result;
        }
    }

    /// <inheritdoc />
    public async Task SetBulkAsync<T>(
        string scope,
        string queryType,
        IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items,
        string expectedGenerationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryType);
        ArgumentNullException.ThrowIfNull(items);

        if (!IsEnabled || _multiplexer == null || items.Count == 0 || string.IsNullOrWhiteSpace(expectedGenerationToken))
        {
            return;
        }

        var tasks = new List<Task<bool>>();
        foreach (var (inputHash, value, expiresAtUtc) in items)
        {
            tasks.Add(SetAsync(scope, queryType, inputHash, value, expectedGenerationToken, expiresAtUtc, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task<Result<T>> GetOrCreateAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await GetAsync<T>(scope, queryType, inputHash, cancellationToken);
        if (cached.IsHit && cached.Value is not null)
        {
            return Result.Success(cached.Value);
        }

        var gateKey = $"{scope}:{queryType}:{inputHash}";
        var gate = AcquireGate(gateKey);

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            try
            {
                // Recheck cache sau khi vào gate
                var recheck = await GetAsync<T>(scope, queryType, inputHash, cancellationToken);
                if (recheck.IsHit && recheck.Value is not null)
                {
                    return Result.Success(recheck.Value);
                }

                // Giữ generation từ lần kiểm tra này trước khi đọc nguồn
                var capturedGen = string.IsNullOrWhiteSpace(recheck.GenerationToken)
                    ? cached.GenerationToken
                    : recheck.GenerationToken;

                bool acquiredPermit = false;
                if (!HasFillPermitHolder.Value)
                {
                    if (Interlocked.Increment(ref _waitingFillCount) > MaxWaitingFillRequests)
                    {
                        Interlocked.Decrement(ref _waitingFillCount);
                        return Error.Unavailable("CACHE_CONCURRENCY_EXCEEDED", "Hàng đợi xử lý tải cache đã đầy. Vui lòng thử lại sau.");
                    }

                    try
                    {
                        await _sourceFillSemaphore.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _waitingFillCount);
                    }

                    HasFillPermitHolder.Value = true;
                    acquiredPermit = true;
                }

                try
                {
                    var factoryResult = await factory(capturedGen, cancellationToken);
                    if (factoryResult.IsFailure)
                    {
                        return factoryResult.Error;
                    }

                    var read = factoryResult.Value;
                    if (read.ExpiresAtUtc.HasValue && !string.IsNullOrWhiteSpace(capturedGen))
                    {
                        await SetAsync(scope, queryType, inputHash, read.Value, capturedGen, read.ExpiresAtUtc.Value, cancellationToken);
                    }

                    return Result.Success(read.Value);
                }
                finally
                {
                    if (acquiredPermit)
                    {
                        HasFillPermitHolder.Value = false;
                        _sourceFillSemaphore.Release();
                    }
                }
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseGate(gateKey, gate);
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(
        IReadOnlyList<CacheTarget> targets,
        Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(factory);

        var distinctTargets = targets.Distinct().ToList();
        var resultMap = new Dictionary<CacheTarget, T>();
        if (distinctTargets.Count == 0)
        {
            return Result.Success<IReadOnlyDictionary<CacheTarget, T>>(resultMap);
        }

        var targetsByScope = distinctTargets.GroupBy(t => (t.Scope, t.QueryType));
        var missingTargets = new List<CacheTarget>();

        foreach (var group in targetsByScope)
        {
            var hashes = group.Select(t => t.InputHash).ToList();
            var bulkResult = await GetBulkAsync<T>(group.Key.Scope, group.Key.QueryType, hashes, cancellationToken);
            foreach (var target in group)
            {
                if (bulkResult.TryGetValue(target.InputHash, out var read) && read.IsHit && read.Value is not null)
                {
                    resultMap[target] = read.Value;
                }
                else
                {
                    missingTargets.Add(target);
                }
            }
        }

        if (missingTargets.Count == 0)
        {
            return Result.Success<IReadOnlyDictionary<CacheTarget, T>>(resultMap);
        }

        var sortedMisses = missingTargets.OrderBy(t => t.CacheKey, StringComparer.Ordinal).ToList();
        var acquiredGates = new List<(string Key, KeyGate Gate)>();
        foreach (var target in sortedMisses)
        {
            var gateKey = target.CacheKey;
            var gate = AcquireGate(gateKey);
            acquiredGates.Add((gateKey, gate));
            await gate.Semaphore.WaitAsync(cancellationToken);
        }

        try
        {
            var stillMissing = new List<CacheTarget>();
            var stillByScope = sortedMisses.GroupBy(t => (t.Scope, t.QueryType));

            foreach (var group in stillByScope)
            {
                var hashes = group.Select(t => t.InputHash).ToList();
                var recheck = await GetBulkAsync<T>(group.Key.Scope, group.Key.QueryType, hashes, cancellationToken);
                foreach (var target in group)
                {
                    if (recheck.TryGetValue(target.InputHash, out var read) && read.IsHit && read.Value is not null)
                    {
                        resultMap[target] = read.Value;
                    }
                    else
                    {
                        stillMissing.Add(target);
                    }
                }
            }

            if (stillMissing.Count == 0)
            {
                return Result.Success<IReadOnlyDictionary<CacheTarget, T>>(resultMap);
            }

            bool acquiredPermit = false;
            if (!HasFillPermitHolder.Value)
            {
                if (Interlocked.Increment(ref _waitingFillCount) > MaxWaitingFillRequests)
                {
                    Interlocked.Decrement(ref _waitingFillCount);
                    return Error.Unavailable("CACHE_CONCURRENCY_EXCEEDED", "Hàng đợi xử lý tải cache đã đầy. Vui lòng thử lại sau.");
                }

                try
                {
                    await _sourceFillSemaphore.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _waitingFillCount);
                }

                HasFillPermitHolder.Value = true;
                acquiredPermit = true;
            }

            try
            {
                var factoryResult = await factory(stillMissing, cancellationToken);
                if (factoryResult.IsFailure)
                {
                    return factoryResult.Error;
                }

                var produced = factoryResult.Value;
                var itemsByScope = produced
                    .Where(p => p.Value.ExpiresAtUtc.HasValue)
                    .GroupBy(p => (p.Key.Scope, p.Key.QueryType));

                foreach (var group in itemsByScope)
                {
                    var readSample = await GetAsync<T>(group.Key.Scope, group.Key.QueryType, group.First().Key.InputHash, cancellationToken);
                    var genToken = readSample.GenerationToken;
                    if (!string.IsNullOrWhiteSpace(genToken))
                    {
                        var itemsToSet = group
                            .Select(p => (p.Key.InputHash, p.Value.Value, p.Value.ExpiresAtUtc!.Value))
                            .ToList();

                        await SetBulkAsync(group.Key.Scope, group.Key.QueryType, itemsToSet, genToken, cancellationToken);
                    }
                }

                foreach (var (target, read) in produced)
                {
                    resultMap[target] = read.Value;
                }

                return Result.Success<IReadOnlyDictionary<CacheTarget, T>>(resultMap);
            }
            finally
            {
                if (acquiredPermit)
                {
                    HasFillPermitHolder.Value = false;
                    _sourceFillSemaphore.Release();
                }
            }
        }
        finally
        {
            for (int i = acquiredGates.Count - 1; i >= 0; i--)
            {
                var (gateKey, gate) = acquiredGates[i];
                gate.Semaphore.Release();
                ReleaseGate(gateKey, gate);
            }
        }
    }

    /// <inheritdoc />
    public async Task InvalidateScopeAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var scopeType = GetScopeType(scope);
        if (!_options.Enabled)
        {
            return;
        }

        if (_multiplexer == null || !_multiplexer.IsConnected)
        {
            throw new RedisException("Không thể kết nối tới Valkey để invalidate cache.");
        }

        try
        {
            var db = _multiplexer.GetDatabase();
            var genKey = GetGenKey(scope);
            var newGen = Guid.NewGuid().ToString("N");
            int genTtlMs = GetDefaultGenerationTtlMs();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            await db.ScriptEvaluateAsync(
                InvalidateLuaScript,
                new[] { genKey },
                new RedisValue[] { newGen, genTtlMs }).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError(scopeType, "all", "invalidate");
            _logger?.LogWarning(ex, "Lỗi invalidate scope {Scope}.", scope);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task InvalidateScopesAsync(
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var distinctScopes = scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        if (distinctScopes.Count == 0)
        {
            return;
        }

        var tasks = distinctScopes.Select(s => InvalidateScopeAsync(s, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private KeyGate AcquireGate(string key)
    {
        return _keyGates.AddOrUpdate(
            key,
            _ => new KeyGate { RefCount = 1 },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.RefCount);
                return existing;
            });
    }

    private void ReleaseGate(string key, KeyGate gate)
    {
        if (Interlocked.Decrement(ref gate.RefCount) <= 0)
        {
            _keyGates.TryRemove(new KeyValuePair<string, KeyGate>(key, gate));
        }
    }

    private (RedisKey GenKey, RedisKey EntryKey) GetKeys(string scope, string queryType, string inputHash)
    {
        var tag = GetScopeTag(scope);
        return (new RedisKey($"{tag}:gen"), new RedisKey($"{tag}:d:{queryType}:{inputHash}"));
    }

    private RedisKey GetGenKey(string scope) => new($"{GetScopeTag(scope)}:gen");

    private RedisKey GetEntryKey(string scope, string queryType, string inputHash) =>
        new($"{GetScopeTag(scope)}:d:{queryType}:{inputHash}");

    private string GetScopeTag(string scope)
    {
        var env = string.IsNullOrWhiteSpace(_options.EnvironmentName) ? "development" : _options.EnvironmentName.ToLowerInvariant();
        return $"{{inventory:{env}:read:v2:scope:{scope}}}";
    }

    private static string GetScopeType(string scope)
    {
        int colonIdx = scope.IndexOf(':');
        return colonIdx > 0 ? scope[..colonIdx] : scope;
    }

    public void Dispose()
    {
        _sourceFillSemaphore.Dispose();
    }

    private sealed class KeyGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    internal sealed record CacheEnvelope<T>(
        [property: JsonPropertyName("v")] int SchemaVersion,
        [property: JsonPropertyName("g")] string GenerationToken,
        [property: JsonPropertyName("exp")] DateTimeOffset ExpiresAtUtc,
        [property: JsonPropertyName("d")] T Data
    );
}
