using Dprental.InventoryService.Application.Recommendation;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Recommendation;

/// <summary>
/// Đọc điểm do hệ thống gợi ý cung cấp qua Valkey; lớp này không huấn luyện hay tính điểm hành vi.
/// Đóng băng điểm theo chu kỳ 5 phút để các instance API dùng chung dữ liệu xếp hạng.
/// </summary>
public sealed partial class ValkeyRecommendationRankingReader : IRecommendationRankingReader, IAsyncDisposable, IDisposable
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

    // Nhận kết nối có sẵn để kiểm thử; vòng đời kết nối thuộc bên truyền vào.
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

    public static string GetSnapshotKey(Guid? userId, long cycle) =>
        userId.HasValue && userId.Value != Guid.Empty
            ? $"inv:rec:snapshot:{cycle}:user:{userId.Value:D}"
            : $"inv:rec:snapshot:{cycle}:anon";

    public async Task<RecommendationSnapshot> ReadAsync(Guid? userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        userId = userId == Guid.Empty ? null : userId;
        // Cùng người dùng và chu kỳ có cùng seed. SQL dùng seed để tạo thứ tự có trọng số
        // lặp lại được; thứ tự vẫn có thể đổi nếu dữ liệu nguồn hoặc bộ lọc thay đổi.
        long cycle = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 300;
        string seed = userId.HasValue ? $"{userId.Value:D}:{cycle}" : $"anon:{cycle}";
        long startedAt = _timeProvider.GetTimestamp();
        var result = RecommendationSnapshot.Unavailable(RecommendationReasons.Unavailable, cycle, seed);
        // Giới hạn toàn bộ lần đọc ở 100 ms để dịch vụ gợi ý chậm không giữ request quá lâu.
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            result = _isConfigured
                ? await ReadCycleAsync(userId, cycle, seed, deadline.Token)
                : RecommendationSnapshot.Unavailable(RecommendationReasons.Unconfigured, cycle, seed);
        }
        // Hủy từ request phải truyền ngược lên; timeout nội bộ trở thành trạng thái thiếu gợi ý.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout, cycle, seed);
        }
        catch (Exception ex)
        {
            string reason = ex is RedisTimeoutException ? RecommendationReasons.Timeout
                : IsCorruptOrWrongTypeException(ex) ? RecommendationReasons.Invalid : RecommendationReasons.Unavailable;
            result = RecommendationSnapshot.Unavailable(reason, cycle, seed);
        }
        finally
        {
            RecordLatency(startedAt, result.Outcome);
        }
        return result;
    }

    private async Task<RecommendationSnapshot> ReadCycleAsync(Guid? userId, long cycle, string seed, CancellationToken ct)
    {
        var multiplexer = await GetMultiplexerAsync(ct);
        if (multiplexer?.IsConnected != true)
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Unavailable, cycle, seed);

        var db = multiplexer.GetDatabase();
        string key = GetSnapshotKey(userId, cycle);
        long cycleEndMs = checked((cycle + 1) * 300000L);
        var cached = await AwaitObservedAsync(db.StringGetAsync(key), ct);
        if (!cached.IsNull)
            return DecodeCycle(cached, userId, cycle, seed, cycleEndMs);

        var source = await ReadSourceAsync(db, userId, cycle, seed, ct);
        if (source.Outcome != RecommendationOutcome.Available) return source;
        if (CycleHasEnded(cycleEndMs))
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout, cycle, seed);

        byte[] payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CachedRankingSnapshotDto
        {
            Version = 1,
            Cycle = cycle,
            Seed = seed,
            Personal = source.PersonalInterests.Select(p => new PersonalScoreDto(p.EquipmentTypeId, p.Score)).ToList(),
            Global = source.GlobalPopularity.Select(g => new GlobalScoreDto(g.ListingId, g.Score)).ToList()
        });
        // SET NX chỉ cho một instance thắng; PXAT làm snapshot hết hạn đúng cuối chu kỳ.
        // Instance thua phải đọc bản thắng thay vì dùng điểm vừa đọc riêng, tránh lệch giữa các API.
        var written = await AwaitObservedAsync(db.ExecuteAsync("SET", key, payload, "NX", "PXAT", cycleEndMs), ct);
        if (CycleHasEnded(cycleEndMs))
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout, cycle, seed);
        if (!written.IsNull)
        {
            if (written.ToString() != "OK") throw new InvalidOperationException("Unexpected snapshot SET reply.");
            return source;
        }

        var winner = await AwaitObservedAsync(db.StringGetAsync(key), ct);
        return winner.IsNull
            ? RecommendationSnapshot.Miss(RecommendationReasons.Miss, cycle, seed)
            : DecodeCycle(winner, userId, cycle, seed, cycleEndMs);
    }

    private RecommendationSnapshot DecodeCycle(RedisValue value, Guid? userId, long cycle, string seed, long cycleEndMs)
    {
        if (CycleHasEnded(cycleEndMs))
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Timeout, cycle, seed);
        // Chặn payload quá lớn và kiểm tra hợp đồng trước khi đưa điểm vào truy vấn SQL.
        byte[] bytes = (byte[])value!;
        return bytes.Length <= 64 * 1024 &&
            TryParseSnapshotJson(bytes, cycle, seed, isAnonymous: !userId.HasValue, out var personal, out var global)
            ? RecommendationSnapshot.Available(personal, global, cycle, seed)
            : RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid, cycle, seed);
    }

    private bool CycleHasEnded(long cycleEndMs) => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= cycleEndMs;

    private static async Task<RecommendationSnapshot> ReadSourceAsync(IDatabase db, Guid? userId, long cycle, string seed, CancellationToken ct)
    {
        // Đọc song song tối đa 300 listing phổ biến và 20 loại thiết bị quan tâm để giới hạn tải.
        // Khách ẩn danh không đọc khóa cá nhân; cả hai tập rỗng được phân loại là miss.
        var globalTask = db.SortedSetRangeByRankWithScoresAsync(GlobalPopularityKey, 0, MaxPopularListingsCount - 1, Order.Descending);
        var personalTask = userId.HasValue
            ? db.SortedSetRangeByRankWithScoresAsync($"rec:v1:user:{userId.Value:D}:equipment-types", 0, MaxInterestsCount - 1, Order.Descending)
            : Task.FromResult(Array.Empty<SortedSetEntry>());
        await AwaitObservedAsync(Task.WhenAll(personalTask, globalTask), ct);
        if (!TryParsePersonalEntries(await personalTask, out var personal) || !TryParseGlobalEntries(await globalTask, out var global))
            return RecommendationSnapshot.Unavailable(RecommendationReasons.Invalid, cycle, seed);
        return personal.Count == 0 && global.Count == 0
            ? RecommendationSnapshot.Miss(RecommendationReasons.Miss, cycle, seed)
            : RecommendationSnapshot.Available(personal, global, cycle, seed);
    }

    private static async Task<T> AwaitObservedAsync<T>(Task<T> task, CancellationToken ct)
    {
        // WaitAsync chỉ dừng chờ, không hủy lệnh Redis đang chạy. Quan sát lỗi đến muộn
        // để tác vụ nền không phát sinh exception không được xử lý sau timeout.
        try { return await task.WaitAsync(ct); }
        catch
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
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

}
