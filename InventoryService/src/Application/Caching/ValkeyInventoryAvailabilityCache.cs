using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Clients;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Triển khai IInventoryAvailabilityCache sử dụng Valkey/Redis với StackExchange.Redis theo DPR-150.
/// Hỗ trợ đọc bulk, ghi atomic kèm metadata index theo asset, và atomic invalidation không dùng KEYS/scan.
/// Độ chính xác millisecond cho TTL và index score, kiểm tra tính toàn vẹn payload chặt chẽ.
/// </summary>
public sealed class ValkeyInventoryAvailabilityCache : IInventoryAvailabilityCache
{
    private const string SetMonthLuaScript = @"
local remainingMs = tonumber(ARGV[2])
if remainingMs <= 0 then
    return 0
end
redis.call('PSETEX', KEYS[1], remainingMs, ARGV[1])
redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', tonumber(ARGV[4]))
redis.call('ZADD', KEYS[2], tonumber(ARGV[3]), KEYS[1])
local currentPttl = redis.call('PTTL', KEYS[2])
if currentPttl < remainingMs then
    redis.call('PEXPIRE', KEYS[2], remainingMs)
end
return 1";

    private const string InvalidateLuaScript = @"
local members = redis.call('ZRANGE', KEYS[1], 0, -1)
if members and #members > 0 then
    for i = 1, #members do
        redis.call('DEL', members[i])
    end
end
redis.call('DEL', KEYS[1])
return 1";

    private static readonly Action<ILogger, Guid, Exception?> LogReadError =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(1, "CacheReadError"), "Lỗi đọc bulk cache cho tài sản {AssetId}. Fallback miss.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogWriteError =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(2, "CacheWriteError"), "Lỗi ghi cache cho tài sản {AssetId}, tháng {MonthKey}.");

    private static readonly Action<ILogger, Guid, Exception?> LogInvalidateError =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(3, "CacheInvalidateError"), "Lỗi invalidate cache cho tài sản {AssetId}.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogCorruptPayload =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(4, "CacheCorruptPayload"), "Payload cache bị hỏng hoặc không hợp lệ cho tài sản {AssetId}, tháng {MonthKey}. Bỏ qua như miss.");

    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly InventoryCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ValkeyInventoryAvailabilityCache>? _logger;

    public bool IsEnabled => _options.Enabled && _multiplexer != null;

    public ValkeyInventoryAvailabilityCache(
        IOptions<InventoryCacheOptions> options,
        IConnectionMultiplexer? multiplexer = null,
        TimeProvider? timeProvider = null,
        ILogger<ValkeyInventoryAvailabilityCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? new InventoryCacheOptions();
        _multiplexer = multiplexer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, CachedMonthRanges?>> GetMonthRangesBulkAsync(
        Guid assetId,
        IReadOnlyList<string> monthKeys,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var result = monthKeys.ToDictionary(k => k, _ => (CachedMonthRanges?)null);
        if (!IsEnabled || monthKeys.Count == 0 || _multiplexer == null)
        {
            return result;
        }

        try
        {
            var db = _multiplexer.GetDatabase();
            var redisKeys = monthKeys.Select(k => (RedisKey)$"inventory:asset:{assetId:D}:busy:{k}").ToArray();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            var redisValues = await db.StringGetAsync(redisKeys).WaitAsync(timeoutCts.Token);

            for (int i = 0; i < monthKeys.Count; i++)
            {
                var monthKey = monthKeys[i];
                var val = redisValues[i];
                if (val.IsNullOrEmpty)
                {
                    continue;
                }

                try
                {
                    var payload = JsonSerializer.Deserialize<CachedMonthPayload>((string)val!);
                    if (payload == null || payload.Ranges == null)
                    {
                        if (_logger != null) LogCorruptPayload(_logger, assetId, monthKey, null);
                        continue;
                    }

                    // Validate ranges không chứa null, ngày hợp lệ và nằm trong tháng
                    if (!ValidatePayloadRanges(payload.Ranges, monthKey))
                    {
                        if (_logger != null) LogCorruptPayload(_logger, assetId, monthKey, null);
                        continue;
                    }

                    if (payload.ExpiresAtUtc > nowUtc)
                    {
                        result[monthKey] = new CachedMonthRanges(payload.Ranges, payload.ExpiresAtUtc);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger != null) LogCorruptPayload(_logger, assetId, monthKey, ex);
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
            if (_logger != null) LogReadError(_logger, assetId, ex);
            return result;
        }
    }

    /// <inheritdoc />
    public async Task SetMonthRangesAsync(
        Guid assetId,
        string monthKey,
        IReadOnlyList<BusyRangeDto> ranges,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _multiplexer == null)
        {
            return;
        }

        var now = Dprental.InventoryService.Common.Utils.BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        long remainingMs = (long)(expiresAtUtc - now).TotalMilliseconds;
        if (remainingMs <= 0)
        {
            return;
        }

        try
        {
            var db = _multiplexer.GetDatabase();
            var key = (RedisKey)$"inventory:asset:{assetId:D}:busy:{monthKey}";
            var indexKey = (RedisKey)$"inventory:asset:{assetId:D}:months";

            var payload = new CachedMonthPayload(ranges, expiresAtUtc);
            var json = JsonSerializer.Serialize(payload);

            long expiresMs = expiresAtUtc.ToUnixTimeMilliseconds();
            long nowMs = now.ToUnixTimeMilliseconds();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            await db.ScriptEvaluateAsync(
                SetMonthLuaScript,
                new[] { key, indexKey },
                new RedisValue[] { json, remainingMs, expiresMs, nowMs })
                .WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger != null) LogWriteError(_logger, assetId, monthKey, ex);
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _multiplexer == null)
        {
            return;
        }

        try
        {
            var db = _multiplexer.GetDatabase();
            var indexKey = (RedisKey)$"inventory:asset:{assetId:D}:months";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            await db.ScriptEvaluateAsync(
                InvalidateLuaScript,
                new[] { indexKey },
                Array.Empty<RedisValue>())
                .WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger != null) LogInvalidateError(_logger, assetId, ex);
        }
    }

    private static bool ValidatePayloadRanges(IReadOnlyList<BusyRangeDto> ranges, string monthKey)
    {
        if (!TryParseMonthBounds(monthKey, out var mStart, out var mEnd))
        {
            return false;
        }

        foreach (var r in ranges)
        {
            if (r == null)
            {
                return false;
            }

            if (r.StartDate > r.EndDate)
            {
                return false;
            }

            if (r.StartDate < mStart || r.EndDate > mEnd)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseMonthBounds(string monthKey, out DateOnly monthStart, out DateOnly monthEnd)
    {
        monthStart = default;
        monthEnd = default;
        if (string.IsNullOrWhiteSpace(monthKey) || monthKey.Length != 6)
        {
            return false;
        }

        if (!int.TryParse(monthKey[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(monthKey[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int month))
        {
            return false;
        }

        if (year < 1 || year > 9999 || month < 1 || month > 12)
        {
            return false;
        }

        int daysInMonth = DateTime.DaysInMonth(year, month);
        monthStart = new DateOnly(year, month, 1);
        monthEnd = new DateOnly(year, month, daysInMonth);
        return true;
    }

    internal sealed record CachedMonthPayload(
        [property: JsonPropertyName("ranges")] IReadOnlyList<BusyRangeDto>? Ranges,
        [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc
    );
}
