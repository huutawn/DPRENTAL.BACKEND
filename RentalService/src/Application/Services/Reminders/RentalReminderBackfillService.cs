#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Reminders;

public sealed class RentalReminderBackfillService : IRentalReminderBackfillService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOrderLockManager _lockManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReminderBackfillService> _logger;

    public RentalReminderBackfillService(
        IServiceScopeFactory scopeFactory,
        IOrderLockManager orderLockManager,
        TimeProvider timeProvider,
        ILogger<RentalReminderBackfillService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _lockManager = orderLockManager ?? throw new ArgumentNullException(nameof(orderLockManager));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BackfillResult> BackfillActiveItemsAsync(
        int batchSize = 100,
        string? phase = null,
        Guid? afterItemId = null,
        bool apply = true,
        Action<BackfillProgress>? onBatchCompleted = null,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) batchSize = 100;

        string? normalizedPhase = phase?.Trim().ToLowerInvariant();
        if (afterItemId.HasValue && string.IsNullOrWhiteSpace(normalizedPhase))
        {
            throw new ArgumentException("Phase (handover hoặc return) là bắt buộc khi truyền cursor (afterItemId).", nameof(phase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedPhase) && normalizedPhase is not ("handover" or "return"))
        {
            throw new ArgumentException($"Phase '{phase}' không hợp lệ. Chỉ chấp nhận 'handover' hoặc 'return'.", nameof(phase));
        }

        int totalHandoverCreated = 0;
        int totalReturnCreated = 0;
        int totalBatches = 0;

        bool runHandover = normalizedPhase is null or "handover";
        bool runReturn = true;

        Guid? handoverAfterId = normalizedPhase == "handover" ? afterItemId : null;
        Guid? returnAfterId = normalizedPhase == "return" ? afterItemId : null;

        Guid? lastCommittedItemId = afterItemId;
        string currentPhase = normalizedPhase ?? "handover";

        // 1. Handover phase for AwaitingHandover items
        if (runHandover)
        {
            currentPhase = "handover";
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset batchNow = _timeProvider.GetUtcNow();

                IReadOnlyList<ReminderBackfillCandidate> candidates;
                await using (var candidateScope = _scopeFactory.CreateAsyncScope())
                {
                    var repo = candidateScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>();
                    candidates = await repo.GetHandoverBackfillCandidatesAsync(handoverAfterId, batchSize, cancellationToken);
                }

                if (candidates.Count == 0) break;

                totalBatches++;
                int batchHandoverCreated = 0;
                var orderGroups = candidates.GroupBy(c => c.OrderId).ToList();

                try
                {
                    foreach (var group in orderGroups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Guid orderId = group.Key;

                        await using var orderScope = _scopeFactory.CreateAsyncScope();
                        var repo = orderScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>();
                        var unitOfWork = orderScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                        // 1. Lấy order lock
                        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);

                        // 2. Mở transaction nhỏ
                        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);

                        // 3. Đọc lại trạng thái order/item và lịch hiện có
                        var order = await repo.GetOrderReminderProjectionAsync(orderId, cancellationToken);
                        if (order == null || order.OrderStatus is RentalOrderStatus.Cancelled or RentalOrderStatus.Expired or RentalOrderStatus.Completed)
                        {
                            await tx.CommitAsync(cancellationToken);
                            continue;
                        }

                        var existingSchedules = await repo.GetByOrderIdAsync(orderId, cancellationToken);

                        // 4. Bỏ item đã kết thúc, đổi phase hoặc quá D+30
                        foreach (var candidate in group)
                        {
                            var item = order.Items.FirstOrDefault(i => i.ItemId == candidate.ItemId);
                            if (item == null) continue;

                            // Đổi phase sang InRental hoặc khác -> bỏ qua trong phase handover
                            if (item.Status != RentalItemStatus.AwaitingHandover)
                            {
                                _logger.LogInformation("Item {ItemId} không còn ở trạng thái AwaitingHandover (hiện tại: {Status}), bỏ qua trong phase Handover", item.ItemId, item.Status);
                                continue;
                            }

                            if (existingSchedules.Any(s => s.RentalItemId == item.ItemId && s.ScheduleType == RentalReminderScheduleType.Handover))
                            {
                                continue;
                            }

                            var eval = RentalReminderPolicy.Evaluate(
                                RentalReminderScheduleType.Handover,
                                item.ExpectedStartDate,
                                lastNotifiedDate: null,
                                overdueDayCount: 0,
                                batchNow);

                            if (eval.Action == ReminderPolicyAction.WindowExpired)
                            {
                                _logger.LogInformation("Item {ItemId} Handover schedule skipped: WindowExpired", item.ItemId);
                                continue;
                            }

                            DateTimeOffset? triggerAt = eval.Action switch
                            {
                                ReminderPolicyAction.SendToday => batchNow,
                                ReminderPolicyAction.Reschedule => eval.NextTriggerAt,
                                _ => null
                            };

                            var schedule = RentalReminderSchedule.Create(
                                order.OrderId,
                                item.ItemId,
                                RentalReminderScheduleType.Handover,
                                item.TotalQuantity,
                                triggerAt,
                                batchNow);

                            if (apply)
                            {
                                bool created = await repo.TryCreateScheduleIfMissingAsync(schedule, cancellationToken);
                                if (created) batchHandoverCreated++;
                            }
                            else
                            {
                                batchHandoverCreated++;
                            }
                        }

                        // Commit per order group
                        await tx.CommitAsync(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý batch {Batch} phase Handover. Checkpoint giữ nguyên ở {LastId}", totalBatches, lastCommittedItemId);
                    return new BackfillResult(
                        totalHandoverCreated,
                        totalReturnCreated,
                        totalBatches,
                        LastPhase: currentPhase,
                        LastProcessedItemId: lastCommittedItemId,
                        Succeeded: false,
                        ErrorMessage: ex.Message);
                }

                // Checkpoint chỉ tiến sau khi toàn bộ order trong page xử lý thành công
                handoverAfterId = candidates[^1].ItemId;
                lastCommittedItemId = handoverAfterId;
                totalHandoverCreated += batchHandoverCreated;

                string resumeCmd = $"dotnet run --project RentalService/src/Maintenance -- backfill-reminders {(apply ? "--apply " : "")}--batch-size {batchSize} --phase handover --after-item-id {lastCommittedItemId}";

                _logger.LogInformation("Backfill batch {Batch}: Handover={Count}, Phase=handover, Checkpoint={LastId}. Resume command: {Cmd}",
                    totalBatches, batchHandoverCreated, lastCommittedItemId, resumeCmd);

                onBatchCompleted?.Invoke(new BackfillProgress("handover", totalBatches, batchHandoverCreated, 0, lastCommittedItemId.Value, resumeCmd));
            }

            // Kết thúc handover thành công: tự động chuyển sang return từ đầu với cursor rỗng
            currentPhase = "return";
            returnAfterId = null;
            lastCommittedItemId = null;
        }

        // 2. Return phase for InRental items
        if (runReturn)
        {
            currentPhase = "return";
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset batchNow = _timeProvider.GetUtcNow();

                IReadOnlyList<ReminderBackfillCandidate> candidates;
                await using (var candidateScope = _scopeFactory.CreateAsyncScope())
                {
                    var repo = candidateScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>();
                    candidates = await repo.GetReturnBackfillCandidatesAsync(returnAfterId, batchSize, cancellationToken);
                }

                if (candidates.Count == 0) break;

                totalBatches++;
                int batchReturnCreated = 0;
                var orderGroups = candidates.GroupBy(c => c.OrderId).ToList();

                try
                {
                    foreach (var group in orderGroups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Guid orderId = group.Key;

                        await using var orderScope = _scopeFactory.CreateAsyncScope();
                        var repo = orderScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>();
                        var unitOfWork = orderScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                        // 1. Lấy order lock
                        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);

                        // 2. Mở transaction nhỏ
                        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);

                        // 3. Đọc lại trạng thái order/item, lịch hiện có và số assignment còn phải trả
                        var order = await repo.GetOrderReminderProjectionAsync(orderId, cancellationToken);
                        if (order == null || order.OrderStatus is RentalOrderStatus.Cancelled or RentalOrderStatus.Expired or RentalOrderStatus.Completed)
                        {
                            await tx.CommitAsync(cancellationToken);
                            continue;
                        }

                        var existingSchedules = await repo.GetByOrderIdAsync(orderId, cancellationToken);

                        // 4. Bỏ item đã kết thúc, đổi phase hoặc quá D+30
                        foreach (var candidate in group)
                        {
                            var item = order.Items.FirstOrDefault(i => i.ItemId == candidate.ItemId);
                            if (item == null) continue;

                            if (item.Status != RentalItemStatus.InRental)
                            {
                                _logger.LogInformation("Item {ItemId} không ở trạng thái InRental (hiện tại: {Status}), bỏ qua trong phase Return", item.ItemId, item.Status);
                                continue;
                            }

                            if (existingSchedules.Any(s => s.RentalItemId == item.ItemId && s.ScheduleType == RentalReminderScheduleType.Return))
                            {
                                continue;
                            }

                            int activeRemaining;
                            try
                            {
                                activeRemaining = RentalReturnActiveUnitsCalculator.Calculate(
                                    item.AllAssignmentIds,
                                    item.ReturnedAssignmentIds,
                                    item.ActiveReturnRequestedAssignmentIds);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Item {ItemId} có dữ liệu assignment không hợp lệ trong lúc backfill Return", item.ItemId);
                                continue;
                            }

                            var eval = RentalReminderPolicy.Evaluate(
                                RentalReminderScheduleType.Return,
                                item.ExpectedEndDate,
                                lastNotifiedDate: null,
                                overdueDayCount: 0,
                                batchNow);

                            if (eval.Action == ReminderPolicyAction.WindowExpired)
                            {
                                _logger.LogInformation("Item {ItemId} Return schedule skipped: WindowExpired", item.ItemId);
                                continue;
                            }

                            DateTimeOffset? triggerAt = activeRemaining > 0
                                ? (eval.Action switch
                                {
                                    ReminderPolicyAction.SendToday => batchNow,
                                    ReminderPolicyAction.Reschedule => eval.NextTriggerAt,
                                    _ => null
                                })
                                : null;

                            var schedule = RentalReminderSchedule.Create(
                                order.OrderId,
                                item.ItemId,
                                RentalReminderScheduleType.Return,
                                activeRemaining,
                                triggerAt,
                                batchNow);

                            if (apply)
                            {
                                bool created = await repo.TryCreateScheduleIfMissingAsync(schedule, cancellationToken);
                                if (created) batchReturnCreated++;
                            }
                            else
                            {
                                batchReturnCreated++;
                            }
                        }

                        // Commit per order group
                        await tx.CommitAsync(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý batch {Batch} phase Return. Checkpoint giữ nguyên ở {LastId}", totalBatches, lastCommittedItemId);
                    return new BackfillResult(
                        totalHandoverCreated,
                        totalReturnCreated,
                        totalBatches,
                        LastPhase: currentPhase,
                        LastProcessedItemId: lastCommittedItemId,
                        Succeeded: false,
                        ErrorMessage: ex.Message);
                }

                // Checkpoint chỉ tiến sau khi toàn bộ order trong page xử lý thành công
                returnAfterId = candidates[^1].ItemId;
                lastCommittedItemId = returnAfterId;
                totalReturnCreated += batchReturnCreated;

                string resumeCmd = $"dotnet run --project RentalService/src/Maintenance -- backfill-reminders {(apply ? "--apply " : "")}--batch-size {batchSize} --phase return --after-item-id {lastCommittedItemId}";

                _logger.LogInformation("Backfill batch {Batch}: Return={Count}, Phase=return, Checkpoint={LastId}. Resume command: {Cmd}",
                    totalBatches, batchReturnCreated, lastCommittedItemId, resumeCmd);

                onBatchCompleted?.Invoke(new BackfillProgress("return", totalBatches, 0, batchReturnCreated, lastCommittedItemId.Value, resumeCmd));
            }
        }

        return new BackfillResult(totalHandoverCreated, totalReturnCreated, totalBatches, currentPhase, lastCommittedItemId, Succeeded: true);
    }
}
