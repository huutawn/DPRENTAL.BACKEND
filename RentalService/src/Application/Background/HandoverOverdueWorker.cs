#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Background;

public sealed record HandoverOverdueBatchResult(int Orders, int ProcessedItems, int AutoCancelled, int NoticesSent, int Failed);

public sealed class HandoverOverdueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HandoverOverdueWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;

    public HandoverOverdueWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<HandoverOverdueOptions> options,
        ILogger<HandoverOverdueWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        HandoverOverdueOptions configured = options.Value;
        _enabled = configured.Enabled;
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, configured.ScanIntervalSeconds));
        _batchSize = Math.Max(1, configured.BatchSize);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("HandoverOverdueWorker đang tắt.");
            return;
        }
        _logger.LogInformation("HandoverOverdueWorker started, interval {Interval}s", _pollingInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in HandoverOverdueWorker loop");
            }

            try
            {
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _logger.LogInformation("HandoverOverdueWorker stopping");
    }

    public async Task<HandoverOverdueBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> orderIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IHandoverOverdueService>();
            orderIds = await service.GetDueOrderIdsAsync(_batchSize, cancellationToken);
        }

        if (orderIds.Count == 0) return new(0, 0, 0, 0, 0);

        int processedItems = 0, autoCancelled = 0, noticesSent = 0, failed = 0;
        foreach (Guid orderId in orderIds)
        {
            try
            {
                using var orderScope = _scopeFactory.CreateScope();
                var service = orderScope.ServiceProvider.GetRequiredService<IHandoverOverdueService>();
                var r = await service.ProcessOrderAsync(orderId, cancellationToken);

                processedItems += r.ProcessedItems;
                autoCancelled += r.AutoCancelled;
                noticesSent += r.NoticesSent;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi xử lý quá hạn bàn giao cho đơn {OrderId}", orderId);
                failed++;
            }
        }

        _logger.LogInformation(
            "HandoverOverdue scan: orders={Orders} processed={Processed} cancelled={Cancelled} notices={Notices} failed={Failed}",
            orderIds.Count, processedItems, autoCancelled, noticesSent, failed);

        return new(orderIds.Count, processedItems, autoCancelled, noticesSent, failed);
    }
}
