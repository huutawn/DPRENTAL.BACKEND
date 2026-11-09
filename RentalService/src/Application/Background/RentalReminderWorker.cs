#pragma warning disable CA1848, CA1873
using System.Diagnostics;
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
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
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _leaseDuration;
    private readonly int _batchSize;
    private readonly double _minClaimFailureBackoffSeconds;
    private readonly double _maxClaimFailureBackoffSeconds;
    private readonly double _baseRetryDelaySeconds;
    private readonly double _maxRetryDelaySeconds;
    private readonly string _workerId;
    private const int MaxRatePerSecond = 100;

    public RentalReminderWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<RentalReminderOptions> options,
        ILogger<RentalReminderWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configured = options?.Value ?? new RentalReminderOptions();
        _enabled = configured.Enabled;
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, configured.ScanIntervalSeconds));
        _leaseDuration = TimeSpan.FromSeconds(Math.Max(5, configured.LeaseDurationSeconds));
        _batchSize = Math.Max(1, configured.BatchSize);
        _minClaimFailureBackoffSeconds = configured.MinClaimFailureBackoffSeconds;
        _maxClaimFailureBackoffSeconds = configured.MaxClaimFailureBackoffSeconds;
        _baseRetryDelaySeconds = Math.Max(1, configured.BaseRetryDelaySeconds);
        _maxRetryDelaySeconds = Math.Max(1, configured.MaxRetryDelaySeconds);
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

        int consecutiveClaimFailures = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ReminderBatchResult result;
                var sw = Stopwatch.StartNew();

                try
                {
                    result = await ProcessBatchAsync(stoppingToken);
                    sw.Stop();
                    consecutiveClaimFailures = 0; // Reset số lần lỗi liên tiếp sau một lần claim thành công, kể cả batch rỗng
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveClaimFailures++;
                    _logger.LogError(ex, "Claim due batch failed (Operation: ClaimDueBatch, ErrorType: {ErrorType}, FailureCount: {FailureCount})",
                        ex.GetType().Name, consecutiveClaimFailures);

                    // Delay lũy thừa từ 5 giây đến 60 giây, thêm jitter 0–20%
                    double minBackoff = _minClaimFailureBackoffSeconds > 0 ? _minClaimFailureBackoffSeconds : 0.05;
                    double maxBackoff = _maxClaimFailureBackoffSeconds > 0 ? _maxClaimFailureBackoffSeconds : 60.0;
                    double baseSec = Math.Min(maxBackoff, minBackoff * Math.Pow(2, Math.Min(consecutiveClaimFailures - 1, 10)));
                    double jitter = 1.0 + (Random.Shared.NextDouble() * 0.20);
                    TimeSpan failureDelay = TimeSpan.FromSeconds(baseSec * jitter);

                    try
                    {
                        await Task.Delay(failureDelay, _timeProvider, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    continue;
                }

                // Rate limiting 100 lịch/giây/replica
                if (result.Claimed > 0)
                {
                    var minDuration = TimeSpan.FromSeconds((double)result.Claimed / MaxRatePerSecond);
                    if (sw.Elapsed < minDuration)
                    {
                        await Task.Delay(minDuration - sw.Elapsed, _timeProvider, stoppingToken);
                    }
                }

                // Nếu số lượng claim đạt batch size, tiếp tục xử lý backlog ngay
                if (result.Claimed >= _batchSize)
                {
                    continue;
                }

                await Task.Delay(_pollingInterval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            _logger.LogInformation("RentalReminderWorker stopped.");
        }
    }

    public async Task<ReminderBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ReminderClaimDescriptor> claimedDescriptors;

        // Scope riêng để claim batch (không giữ DbContext hoặc transaction trong thời gian delay)
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

            ProcessOrderGroupResult orderResult;

            // Scope & DbContext hoàn toàn riêng biệt cho mỗi nhóm order
            await using (var orderScope = _scopeFactory.CreateAsyncScope())
            {
                var processor = orderScope.ServiceProvider.GetRequiredService<IRentalReminderBatchProcessor>();

                try
                {
                    orderResult = await processor.ProcessOrderReminderGroupAsync(orderId, descriptors, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi unhandled khi xử lý nhóm order {OrderId}", orderId);
                    var fallbackResults = descriptors.Select(d => new ReminderItemProcessResult(
                        d.ScheduleId,
                        d.LeaseToken,
                        ReminderItemProcessOutcome.RetryableFailure,
                        ex.Message)).ToList();
                    orderResult = new ProcessOrderGroupResult(descriptors.Count, 0, 0, descriptors.Count, fallbackResults);
                }
            }
            // orderScope is now disposed and transaction was rolled back if error!

            totalProcessed += orderResult.Processed;
            totalNoticesSent += orderResult.NoticesSent;
            totalCompleted += orderResult.Completed;
            totalFailed += orderResult.Failed;

            // Xử lý retry/failure trong scope mới sau khi transaction của order đã rollback
            var failedItems = orderResult.ItemResults
                .Where(r => r.Outcome is ReminderItemProcessOutcome.RetryableFailure or ReminderItemProcessOutcome.PermanentFailure)
                .ToList();

            if (failedItems.Count > 0)
            {
                await HandleFailuresInFreshScopeAsync(failedItems, descriptors, cancellationToken);
            }
        }

        return new ReminderBatchResult(claimedDescriptors.Count, totalProcessed, totalNoticesSent, totalCompleted, totalFailed);
    }

    private async Task HandleFailuresInFreshScopeAsync(
        List<ReminderItemProcessResult> failedItems,
        List<ReminderClaimDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var descriptorMap = descriptors.ToDictionary(d => d.ScheduleId);

        await using var retryScope = _scopeFactory.CreateAsyncScope();
        var scheduleRepo = retryScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>();

        var failedIds = failedItems.Select(f => f.ScheduleId).Distinct().ToList();
        var scheduleStates = (await scheduleRepo.GetScheduleStatesAsync(failedIds, cancellationToken)) ?? new Dictionary<Guid, ReminderScheduleStateProjection>();

        foreach (var item in failedItems)
        {
            if (!descriptorMap.TryGetValue(item.ScheduleId, out var desc))
                continue;

            // Kiểm tra trạng thái và ownership dưới thời gian hiện tại
            if (!scheduleStates.TryGetValue(item.ScheduleId, out var state) ||
                state.Status != RentalReminderScheduleStatus.Active ||
                state.LeaseToken != desc.LeaseToken ||
                state.LeasedUntil == null ||
                state.LeasedUntil < now)
            {
                _logger.LogInformation("Schedule {ScheduleId} đã mất lease hoặc đổi lifecycle, bỏ qua ghi retry", item.ScheduleId);
                continue;
            }

            if (item.Outcome == ReminderItemProcessOutcome.PermanentFailure)
            {
                // Lỗi dữ liệu không thể sửa bằng retry: hoãn đến 08:00 ngày tiếp theo và ghi error metric/log
                _logger.LogError("Permanent failure cho schedule {ScheduleId}: {Reason}", item.ScheduleId, item.ErrorReason);
                DateTimeOffset next8Am = RentalReminderPolicy.GetNextVietnam8Am(now);
                await scheduleRepo.ReleaseLeaseWithTokenAsync(desc.ScheduleId, desc.LeaseToken, next8Am, now, cancellationToken);
            }
            else // RetryableFailure
            {
                // Transient retry: 30 giây, 60 giây, 120 giây..., tối đa 3.600 giây, thêm jitter 0–20%
                int retryCount = state.RetryCount;
                double baseSeconds = Math.Min(_maxRetryDelaySeconds, _baseRetryDelaySeconds * Math.Pow(2, Math.Min(retryCount, 10)));
                double jitter = 1.0 + (Random.Shared.NextDouble() * 0.20);
                DateTimeOffset nextRetryAt = now.AddSeconds(baseSeconds * jitter);

                bool recorded = await scheduleRepo.FailScheduleWithLeaseCheckAsync(
                    desc.ScheduleId,
                    desc.LeaseToken,
                    nextRetryAt,
                    now,
                    cancellationToken);

                if (!recorded)
                {
                    _logger.LogInformation("LeaseLost khi ghi retry cho schedule {ScheduleId}", desc.ScheduleId);
                }
            }
        }
    }
}
