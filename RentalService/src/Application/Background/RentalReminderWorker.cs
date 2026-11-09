#pragma warning disable CA1848, CA1873
using System.Diagnostics;
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Background;

public sealed record ReminderBatchResult(int Claimed, int Processed, int NoticesSent, int Completed, int Failed);

public sealed class RentalReminderWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RentalReminderWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _leaseDuration;
    private readonly int _batchSize;
    private readonly string _workerId;
    private const int MaxRatePerSecond = 100;

    public RentalReminderWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<RentalReminderOptions> options,
        ILogger<RentalReminderWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configured = options?.Value ?? new RentalReminderOptions();
        _enabled = configured.Enabled;
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, configured.ScanIntervalSeconds));
        _leaseDuration = TimeSpan.FromSeconds(Math.Max(5, configured.LeaseDurationSeconds));
        _batchSize = Math.Max(1, configured.BatchSize);
        _workerId = $"rental-reminder-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("RentalReminderWorker is disabled.");
            return;
        }

        _logger.LogInformation("RentalReminderWorker started with worker ID {WorkerId}, poll interval {Interval}s, batch size {BatchSize}",
            _workerId, _pollingInterval.TotalSeconds, _batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            var result = await ProcessBatchAsync(stoppingToken);
            sw.Stop();

            // Nếu số lượng claim đạt batch size, tiếp tục xử lý backlog ngay nhưng có rate limiting 100 lịch/giây
            if (result.Claimed >= _batchSize)
            {
                var minDuration = TimeSpan.FromSeconds((double)result.Claimed / MaxRatePerSecond);
                if (sw.Elapsed < minDuration)
                {
                    await Task.Delay(minDuration - sw.Elapsed, stoppingToken);
                }
                continue;
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }

        _logger.LogInformation("RentalReminderWorker stopped.");
    }

    public async Task<ReminderBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ReminderClaimDescriptor> claimedDescriptors;

        // Scope riêng để claim batch
        await using (var claimScope = _scopeFactory.CreateAsyncScope())
        {
            var scheduleRepo = claimScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>();
            claimedDescriptors = await scheduleRepo.ClaimDueBatchAsync(_workerId, _leaseDuration, _batchSize, cancellationToken);
        }

        if (claimedDescriptors.Count == 0)
        {
            return new ReminderBatchResult(0, 0, 0, 0, 0);
        }

        int totalProcessed = 0;
        int totalNoticesSent = 0;
        int totalCompleted = 0;
        int totalFailed = 0;

        // Gom các claim descriptors theo RentalOrderId
        var orderGroups = claimedDescriptors.GroupBy(d => d.OrderId);

        foreach (var group in orderGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid orderId = group.Key;
            var descriptors = group.ToList();

            // Scope & DbContext hoàn toàn riêng biệt cho mỗi nhóm order
            await using var orderScope = _scopeFactory.CreateAsyncScope();
            var processor = orderScope.ServiceProvider.GetRequiredService<IRentalReminderBatchProcessor>();

            try
            {
                var result = await processor.ProcessOrderReminderGroupAsync(orderId, descriptors, cancellationToken);
                totalProcessed += result.Processed;
                totalNoticesSent += result.NoticesSent;
                totalCompleted += result.Completed;
                totalFailed += result.Failed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                totalFailed += descriptors.Count;
                _logger.LogError(ex, "Lỗi unhandled khi xử lý nhóm order {OrderId}", orderId);
            }
        }

        return new ReminderBatchResult(claimedDescriptors.Count, totalProcessed, totalNoticesSent, totalCompleted, totalFailed);
    }
}
