using System.Diagnostics;
using Dprental.InventoryService.Application.Recommendation;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Recommendation;

public sealed class ValkeyRecommendationRankingReader : IRecommendationRankingReader, IAsyncDisposable, IDisposable
{
    private const string GlobalPopularityKey = "rec:v1:global:popular-listings";
    private const int MaxInterestsCount = 20;
    private const int MaxPopularListingsCount = 300;
    private const long MaxValidScore = 1L << 53; // 9,007,199,254,740,992
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(100);

    private readonly bool _isConfigured;
    private readonly ConfigurationOptions? _configurationOptions;
    private readonly RecommendationMetrics _metrics;
    private readonly ILogger<ValkeyRecommendationRankingReader> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private IConnectionMultiplexer? _multiplexer;
    private readonly bool _ownsMultiplexer;
    private bool _disposed;

    public ValkeyRecommendationRankingReader(
        IConfiguration configuration,
        RecommendationMetrics? metrics = null,
        ILogger<ValkeyRecommendationRankingReader>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _metrics = metrics ?? new RecommendationMetrics();
        _logger = logger ?? NullLogger<ValkeyRecommendationRankingReader>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsMultiplexer = true;

        string? connectionString = configuration.GetConnectionString("RecommendationRedis");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _isConfigured = false;
            return;
        }

        try
        {
            _configurationOptions = ConfigurationOptions.Parse(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cấu hình chuỗi kết nối 'ConnectionStrings:RecommendationRedis' không hợp lệ.", ex);
        }

        _configurationOptions.AbortOnConnectFail = false;
        _configurationOptions.ConnectTimeout = (int)DefaultTimeout.TotalMilliseconds;
        _configurationOptions.SyncTimeout = (int)DefaultTimeout.TotalMilliseconds;
        _configurationOptions.AsyncTimeout = (int)DefaultTimeout.TotalMilliseconds;
        _configurationOptions.KeepAlive = 60;
        _isConfigured = true;
    }

    // Constructor for testing with pre-existing multiplexer
    public ValkeyRecommendationRankingReader(
        IConnectionMultiplexer multiplexer,
        RecommendationMetrics? metrics = null,
        ILogger<ValkeyRecommendationRankingReader>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _metrics = metrics ?? new RecommendationMetrics();
        _logger = logger ?? NullLogger<ValkeyRecommendationRankingReader>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isConfigured = true;
        _ownsMultiplexer = false;
    }

    public async Task<RecommendationSnapshot> ReadAsync(Guid? userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isConfigured)
        {
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Unconfigured);
        }

        long startTimestamp = _timeProvider.GetTimestamp();

        using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var multiplexer = await GetMultiplexerAsync(linkedCts.Token);
            if (multiplexer == null || !multiplexer.IsConnected)
            {
                RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                return RecommendationSnapshot.Unavailable(RecommendationReasons.Unavailable);
            }

            var db = multiplexer.GetDatabase();

            if (userId.HasValue && userId.Value != Guid.Empty)
            {
                string userKey = $"rec:v1:user:{userId.Value:D}:equipment-types";
                var personalTask = db.SortedSetRangeByRankWithScoresAsync(userKey, 0, MaxInterestsCount - 1, Order.Descending);
                var globalTask = db.SortedSetRangeByRankWithScoresAsync(GlobalPopularityKey, 0, MaxPopularListingsCount - 1, Order.Descending);

                // Wait with single shared deadline
                try
                {
                    await Task.WhenAll(personalTask, globalTask).WaitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Observe tasks to prevent unobserved task exceptions
                    ObserveBackgroundTask(personalTask);
                    ObserveBackgroundTask(globalTask);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                    return RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout);
                }
                catch (Exception ex)
                {
                    ObserveBackgroundTask(personalTask);
                    ObserveBackgroundTask(globalTask);

                    RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                    if (IsCorruptOrWrongTypeException(ex))
                    {
                        return RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid);
                    }
                    return RecommendationSnapshot.Unavailable(RecommendationReasons.Unavailable);
                }

                var personalRaw = await personalTask;
                var globalRaw = await globalTask;

                if (!TryParsePersonalEntries(personalRaw, out var personalScores) ||
                    !TryParseGlobalEntries(globalRaw, out var globalScores))
                {
                    RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                    return RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid);
                }

                if (personalScores.Count == 0 && globalScores.Count == 0)
                {
                    RecordLatency(startTimestamp, RecommendationOutcome.Miss);
                    return RecommendationSnapshot.Miss(RecommendationReasons.Miss);
                }

                RecordLatency(startTimestamp, RecommendationOutcome.Available);
                return RecommendationSnapshot.Available(personalScores, globalScores);
            }
            else
            {
                var globalTask = db.SortedSetRangeByRankWithScoresAsync(GlobalPopularityKey, 0, MaxPopularListingsCount - 1, Order.Descending);

                try
                {
                    await globalTask.WaitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    ObserveBackgroundTask(globalTask);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                    return RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout);
                }
                catch (Exception ex)
                {
                    ObserveBackgroundTask(globalTask);

                    RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                    if (IsCorruptOrWrongTypeException(ex))
                    {
                        return RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid);
                    }
                    return RecommendationSnapshot.Unavailable(RecommendationReasons.Unavailable);
                }

                var globalRaw = await globalTask;
                if (!TryParseGlobalEntries(globalRaw, out var globalScores))
                {
                    RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
                    return RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid);
                }

                if (globalScores.Count == 0)
                {
                    RecordLatency(startTimestamp, RecommendationOutcome.Miss);
                    return RecommendationSnapshot.Miss(RecommendationReasons.Miss);
                }

                RecordLatency(startTimestamp, RecommendationOutcome.Available);
                return RecommendationSnapshot.Available([], globalScores);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout);
        }
        catch (Exception ex)
        {
            RecordLatency(startTimestamp, RecommendationOutcome.Unavailable);
            if (IsCorruptOrWrongTypeException(ex))
            {
                return RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid);
            }
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Unavailable);
        }
    }

    private static void ObserveBackgroundTask<T>(Task<T> task)
    {
        if (task.IsCompleted) return;
        _ = task.ContinueWith(t =>
        {
            _ = t.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void RecordLatency(long startTimestamp, RecommendationOutcome outcome)
    {
        double latencyMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
        string outcomeStr = outcome switch
        {
            RecommendationOutcome.Available => RecommendationReasons.Available,
            RecommendationOutcome.Miss => RecommendationReasons.Miss,
            _ => RecommendationReasons.Unavailable
        };
        _metrics.RecordLatency(latencyMs, outcomeStr);
    }

    private static bool IsCorruptOrWrongTypeException(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(IsCorruptOrWrongTypeException);
        }
        if (ex is RedisServerException serverEx &&
            (serverEx.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase) ||
             serverEx.Message.Contains("syntax error", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }

    private static bool TryParsePersonalEntries(
        SortedSetEntry[] entries,
        out IReadOnlyList<EquipmentTypeInterestScore> scores)
    {
        scores = [];
        if (entries.Length == 0) return true;
        if (entries.Length > MaxInterestsCount) return false;

        var list = new List<EquipmentTypeInterestScore>(entries.Length);
        var seen = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (!TryParseCanonicalGuid(entry.Element, out var id) || id == Guid.Empty)
                return false;

            if (!seen.Add(id))
                return false;

            if (!IsValidScore(entry.Score, out long longScore))
                return false;

            list.Add(new EquipmentTypeInterestScore(id, longScore));
        }

        scores = list;
        return true;
    }

    private static bool TryParseGlobalEntries(
        SortedSetEntry[] entries,
        out IReadOnlyList<ListingPopularityScore> scores)
    {
        scores = [];
        if (entries.Length == 0) return true;
        if (entries.Length > MaxPopularListingsCount) return false;

        var list = new List<ListingPopularityScore>(entries.Length);
        var seen = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (!TryParseCanonicalGuid(entry.Element, out var id) || id == Guid.Empty)
                return false;

            if (!seen.Add(id))
                return false;

            if (!IsValidScore(entry.Score, out long longScore))
                return false;

            list.Add(new ListingPopularityScore(id, longScore));
        }

        scores = list;
        return true;
    }

    private static bool TryParseCanonicalGuid(RedisValue value, out Guid id)
    {
        id = Guid.Empty;
        if (value.IsNull) return false;
        string? str = value;
        if (string.IsNullOrEmpty(str) || str.Length != 36) return false;

        if (!Guid.TryParseExact(str, "D", out id)) return false;

        // Strictly verify lowercase hex chars and hyphens (canonical "D" format)
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (c != '-') return false;
            }
            else
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
        }

        return id != Guid.Empty;
    }

    private static bool IsValidScore(double score, out long longScore)
    {
        longScore = 0;
        if (double.IsNaN(score) || double.IsInfinity(score)) return false;
        if (score < 1.0 || score > MaxValidScore) return false;
        if (Math.Floor(score) != score) return false;

        longScore = (long)score;
        return true;
    }

    private async Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken)
    {
        if (!_isConfigured) return null;
        if (_multiplexer != null && _multiplexer.IsConnected) return _multiplexer;
        if (!_ownsMultiplexer) return _multiplexer;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_multiplexer != null && _multiplexer.IsConnected) return _multiplexer;

            if (_multiplexer != null)
            {
                try { await _multiplexer.DisposeAsync(); } catch { }
                _multiplexer = null;
            }

            _multiplexer = await ConnectionMultiplexer.ConnectAsync(_configurationOptions!);
            return _multiplexer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Recommendation Valkey.");
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsMultiplexer && _multiplexer != null)
        {
            await _multiplexer.DisposeAsync();
            _multiplexer = null;
        }

        _connectLock.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsMultiplexer && _multiplexer != null)
        {
            _multiplexer.Dispose();
            _multiplexer = null;
        }

        _connectLock.Dispose();
    }
}
