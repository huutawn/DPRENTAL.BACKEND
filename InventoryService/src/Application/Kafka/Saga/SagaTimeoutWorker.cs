using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Kafka.Saga;

/// <summary>
/// Background worker quét và kích hoạt các timeout của Saga đã đến hạn (timeout_deadline &lt;= now).
/// - Dùng deadline persisted trong PostgreSQL và FOR UPDATE SKIP LOCKED để đảm bảo hai worker cạnh tranh không chạy cùng timeout hai lần.
/// - Thực thi timeout callback, cập nhật saga và Outbox commit atomically.
/// - Restart hệ thống không làm mất lịch timeout.
/// </summary>
public sealed class SagaTimeoutWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SagaTimeoutWorker>? _logger;

    public int BatchSize { get; set; } = 10;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public SagaTimeoutWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SagaTimeoutWorker>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("SagaTimeoutWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessDueTimeoutsBatchAsync(stoppingToken);

                if (processed == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                else
                {
                    // Có timeout được xử lý, tiếp tục vòng lặp nhanh để xử lý tiếp nếu còn tồn đọng
                    await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi ngoài dự kiến trong SagaTimeoutWorker loop.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger?.LogInformation("SagaTimeoutWorker đã dừng.");
    }

    /// <summary>
    /// Quét và xử lý một lô timeout đã đến hạn.
    /// Trả về số lượng saga timeout đã xử lý.
    /// </summary>
    public async Task<int> ProcessDueTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sagaStore = scope.ServiceProvider.GetRequiredService<ISagaStore>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISagaOrchestrator>();

        var leaseToken = Guid.NewGuid();

        // Claim các saga quá hạn với FOR UPDATE SKIP LOCKED và timeout lease token
        var dueSagas = await sagaStore.ClaimOverdueTimeoutsAsync(
            BatchSize,
            leaseToken,
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            cancellationToken);

        if (dueSagas.Count == 0)
        {
            return 0;
        }

        foreach (var saga in dueSagas)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (saga.TimeoutId is null) continue;

            try
            {
                var result = await orchestrator.ProcessTimeoutAsync(
                    saga.Id,
                    saga.TimeoutId.Value,
                    leaseToken,
                    cancellationToken);

                if (result.IsFailure)
                {
                    _logger?.LogWarning(
                        "Xử lý timeout cho Saga {Id} ({Type}) thất bại: {Error}",
                        saga.Id, saga.SagaType, result.Error.Message);
                }
                else
                {
                    _logger?.LogDebug(
                        "Đã xử lý timeout thành công cho Saga {Id} ({Type}).",
                        saga.Id, saga.SagaType);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi khi xử lý timeout cho Saga {Id}", saga.Id);
            }
        }

        return dueSagas.Count;
    }
}
