using System.Collections.Concurrent;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Listener nền sử dụng một kết nối Npgsql độc lập để lắng nghe PostgreSQL NOTIFY trên kênh inventory_work_available.
/// Đánh thức các worker trong bộ nhớ qua SemaphoreSlim(0, 1) giúp giới hạn dung lượng và gộp tín hiệu trùng lặp (coalescing).
/// Tự động quét lại backlog ngay sau khi LISTEN thành công (lần đầu hoặc sau khi reconnect).
/// </summary>
public sealed class PostgresQueueNotificationListener : BackgroundService, IQueueNotificationListener
{
    public const string ChannelName = "inventory_work_available";

    public const string PayloadOutbox = "outbox";
    public const string PayloadCacheInvalidation = "cache_invalidation";
    public const string PayloadSearchProjection = "search_projection";

    private readonly string? _connectionString;
    private readonly ILogger<PostgresQueueNotificationListener>? _logger;
    private readonly ConcurrentDictionary<QueueWorkKind, SemaphoreSlim> _semaphores = new();

    private volatile bool _isConnected;

    public PostgresQueueNotificationListener(
        string? connectionString,
        ILogger<PostgresQueueNotificationListener>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger;

        _semaphores[QueueWorkKind.Outbox] = new SemaphoreSlim(0, 1);
        _semaphores[QueueWorkKind.CacheInvalidation] = new SemaphoreSlim(0, 1);
        _semaphores[QueueWorkKind.SearchProjection] = new SemaphoreSlim(0, 1);
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public async ValueTask<bool> WaitForWorkAsync(
        QueueWorkKind queueKind,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero) return true;

        var sem = _semaphores.GetOrAdd(queueKind, _ => new SemaphoreSlim(0, 1));
        return await sem.WaitAsync(timeout, cancellationToken);
    }

    /// <inheritdoc />
    public void TriggerWakeUp(QueueWorkKind queueKind)
    {
        var sem = _semaphores.GetOrAdd(queueKind, _ => new SemaphoreSlim(0, 1));
        lock (sem)
        {
            if (sem.CurrentCount == 0)
            {
                try
                {
                    sem.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Đã có tín hiệu chờ trong sem -> gộp tín hiệu
                }
            }
        }
    }

    /// <inheritdoc />
    public void TriggerAll()
    {
        TriggerWakeUp(QueueWorkKind.Outbox);
        TriggerWakeUp(QueueWorkKind.CacheInvalidation);
        TriggerWakeUp(QueueWorkKind.SearchProjection);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogInformation("PostgresQueueNotificationListener không có connection string; hoạt động ở chế độ ngắt kết nối.");
            _isConnected = false;
            return;
        }

        var retryDelay = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            NpgsqlConnection? conn = null;

            try
            {
                conn = new NpgsqlConnection(_connectionString);
                conn.Notification += OnNotificationReceived;
                await conn.OpenAsync(stoppingToken);

                await using (var cmd = new NpgsqlCommand($"LISTEN {ChannelName};", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }

                _isConnected = true;
                retryDelay = TimeSpan.FromSeconds(1);
                _logger?.LogInformation("PostgresQueueNotificationListener đã kết nối và đăng ký LISTEN {Channel} thành công.", ChannelName);

                // Đăng ký LISTEN thành công -> quét backlog ngay
                TriggerAll();

                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _logger?.LogWarning(ex, "Mất kết nối hoặc lỗi trong PostgresQueueNotificationListener. Thử lại sau {Delay}s.", retryDelay.TotalSeconds);
            }
            finally
            {
                _isConnected = false;
                if (conn != null)
                {
                    try
                    {
                        conn.Notification -= OnNotificationReceived;
                        await conn.DisposeAsync();
                    }
                    catch { }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(retryDelay, stoppingToken);
                    retryDelay = TimeSpan.FromSeconds(Math.Min(10, retryDelay.TotalSeconds * 2));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger?.LogInformation("PostgresQueueNotificationListener đã dừng.");
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        if (!string.Equals(e.Channel, ChannelName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = e.Payload?.Trim().ToLowerInvariant();

        switch (payload)
        {
            case PayloadOutbox:
                TriggerWakeUp(QueueWorkKind.Outbox);
                break;
            case PayloadCacheInvalidation:
                TriggerWakeUp(QueueWorkKind.CacheInvalidation);
                break;
            case PayloadSearchProjection:
                TriggerWakeUp(QueueWorkKind.SearchProjection);
                break;
            default:
                // Nếu notification không có payload cụ thể, đánh thức tất cả
                TriggerAll();
                break;
        }
    }

    public override void Dispose()
    {
        foreach (var sem in _semaphores.Values)
        {
            sem.Dispose();
        }
        base.Dispose();
    }
}
