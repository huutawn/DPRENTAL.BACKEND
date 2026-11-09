#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Reminders;

public sealed class RentalReminderBatchProcessor : IRentalReminderBatchProcessor
{
    private readonly IRentalReminderScheduleRepository _scheduleRepo;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalEventProducer _eventProducer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReminderBatchProcessor> _logger;
    private readonly IUnitOfWork _unitOfWork;

    public RentalReminderBatchProcessor(
        IRentalReminderScheduleRepository scheduleRepo,
        IOrderLockManager lockManager,
        IRentalEventProducer eventProducer,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<RentalReminderBatchProcessor>? logger = null)
    {
        _scheduleRepo = scheduleRepo ?? throw new ArgumentNullException(nameof(scheduleRepo));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _eventProducer = eventProducer ?? throw new ArgumentNullException(nameof(eventProducer));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RentalReminderBatchProcessor>.Instance;
    }

    public async Task<ProcessOrderGroupResult> ProcessOrderReminderGroupAsync(
        Guid orderId,
        IReadOnlyList<ReminderClaimDescriptor> descriptors,
        CancellationToken cancellationToken = default)
    {
        if (descriptors.Count == 0) return new ProcessOrderGroupResult(0, 0, 0, 0);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        int processed = 0;
        int noticesSent = 0;
        int completed = 0;
        int failed = 0;
        var itemResults = new List<ReminderItemProcessResult>(descriptors.Count);

        // 1. Lấy order lock trước khi mở transaction và truy cập dữ liệu
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);

        // 2. Mở transaction trong scope riêng của order này
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            // 3. Khóa các schedule cần xử lý theo thứ tự ID tăng dần (chống deadlock)
            var orderedDescriptors = descriptors.OrderBy(d => d.ScheduleId).ToList();
            var scheduleIds = orderedDescriptors.Select(d => d.ScheduleId).Distinct().ToList();
            await _scheduleRepo.LockSchedulesForUpdateAsync(scheduleIds, cancellationToken);

            // Đọc trạng thái hiện tại của schedule qua abstraction Repository
            var scheduleStates = await _scheduleRepo.GetScheduleStatesAsync(scheduleIds, cancellationToken);

            // 4. Đọc projection tối thiểu của Order
            var order = await _scheduleRepo.GetOrderReminderProjectionAsync(orderId, cancellationToken);
            if (order == null || order.OrderStatus is RentalOrderStatus.Cancelled or RentalOrderStatus.Expired or RentalOrderStatus.Completed)
            {
                foreach (var desc in descriptors)
                {
                    bool ok = await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                    if (ok)
                    {
                        completed++;
                        processed++;
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ReminderItemProcessOutcome.Succeeded));
                    }
                    else
                    {
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ReminderItemProcessOutcome.LeaseLost));
                    }
                }
                await tx.CommitAsync(cancellationToken);
                return new ProcessOrderGroupResult(processed, noticesSent, completed, failed, itemResults);
            }

            foreach (var desc in orderedDescriptors)
            {
                processed++;

                if (!scheduleStates.TryGetValue(desc.ScheduleId, out var state) ||
                    state.Status != RentalReminderScheduleStatus.Active ||
                    state.LeaseToken != desc.LeaseToken ||
                    state.LeasedUntil == null ||
                    state.LeasedUntil < now)
                {
                    itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ReminderItemProcessOutcome.LeaseLost));
                    continue;
                }

                var item = order.Items.FirstOrDefault(i => i.ItemId == desc.ItemId);
                if (item == null)
                {
                    bool ok = await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                    if (ok) completed++;
                    itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Succeeded : ReminderItemProcessOutcome.LeaseLost));
                    continue;
                }

                DateOnly targetDate;
                int activeUnits;

                if (desc.ScheduleType == RentalReminderScheduleType.Handover)
                {
                    if (item.Status != RentalItemStatus.AwaitingHandover)
                    {
                        bool ok = await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                        if (ok) completed++;
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Succeeded : ReminderItemProcessOutcome.LeaseLost));
                        continue;
                    }

                    if (item.HandoverInProgress)
                    {
                        // Handover đang đối soát: hoãn lần xét tiếp 5 phút
                        _logger.LogInformation("Item {ItemId} đang có bàn giao dở dang/đối soát, hoãn lịch nhắc 5 phút", item.ItemId);
                        bool ok = await _scheduleRepo.ReleaseLeaseWithTokenAsync(desc.ScheduleId, desc.LeaseToken, now.AddMinutes(5), now, cancellationToken);
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Deferred : ReminderItemProcessOutcome.LeaseLost));
                        continue;
                    }

                    targetDate = item.ExpectedStartDate;
                    activeUnits = item.TotalQuantity;
                }
                else // Return
                {
                    if (item.Status != RentalItemStatus.InRental)
                    {
                        bool ok = await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                        if (ok) completed++;
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Succeeded : ReminderItemProcessOutcome.LeaseLost));
                        continue;
                    }

                    try
                    {
                        activeUnits = RentalReturnActiveUnitsCalculator.Calculate(
                            item.AllAssignmentIds,
                            item.ReturnedAssignmentIds,
                            item.ActiveReturnRequestedAssignmentIds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Item {ItemId} có dữ liệu assignment không hợp lệ khi tính active units", item.ItemId);
                        failed++;
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ReminderItemProcessOutcome.PermanentFailure, ex.Message));
                        continue;
                    }

                    if (activeUnits <= 0)
                    {
                        // Máy đang chờ xác nhận trả: cập nhật rõ remaining_unit_count=0, next_trigger_at=NULL và xóa lease
                        _logger.LogInformation("Item {ItemId} không còn máy cần trả (đang chờ xác nhận hoặc đã trả hết), tạm dừng lịch nhắc", item.ItemId);
                        bool ok = await _scheduleRepo.PauseScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Deferred : ReminderItemProcessOutcome.LeaseLost));
                        continue;
                    }

                    targetDate = item.ExpectedEndDate;
                }

                // 5. Đánh giá policy
                var eval = RentalReminderPolicy.Evaluate(
                    desc.ScheduleType,
                    targetDate,
                    state.LastNotifiedDate,
                    state.OverdueDayCount,
                    now);

                if (eval.Action == ReminderPolicyAction.WindowExpired)
                {
                    bool ok = await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                    if (ok) completed++;
                    itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Succeeded : ReminderItemProcessOutcome.LeaseLost));
                    continue;
                }

                if (eval.Action == ReminderPolicyAction.Reschedule)
                {
                    bool ok = await _scheduleRepo.ReleaseLeaseWithTokenAsync(desc.ScheduleId, desc.LeaseToken, eval.NextTriggerAt, now, cancellationToken);
                    itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ok ? ReminderItemProcessOutcome.Deferred : ReminderItemProcessOutcome.LeaseLost));
                    continue;
                }

                if (eval.Action == ReminderPolicyAction.SendToday)
                {
                    // Enqueue Outbox event với MessageId tất định
                    var payload = new RentalReminderDuePayload(
                        order.OrderId,
                        item.ItemId,
                        order.OrderCode,
                        order.RenterId,
                        item.SupplierId,
                        eval.ReminderType!,
                        eval.ReminderDate!.Value,
                        eval.ActionDeadline ?? RentalReminderPolicy.ActionDeadline(targetDate),
                        activeUnits,
                        targetDate,
                        order.OrderVersion);

                    var enqueueResult = await _eventProducer.EnqueueRentalReminderDueAsync(payload, cancellationToken: cancellationToken);
                    if (enqueueResult.IsFailure)
                    {
                        throw new InvalidOperationException($"Lỗi enqueue outbox reminder cho item {item.ItemId}: {enqueueResult.Error.Message}");
                    }

                    // Cập nhật tiến lịch có kiểm tra fencing token
                    bool advanced = await _scheduleRepo.AdvanceScheduleWithLeaseCheckAsync(
                        desc.ScheduleId,
                        desc.LeaseToken,
                        eval.ReminderDate.Value,
                        now,
                        eval.NextTriggerAt,
                        eval.IsOverdue,
                        eval.CompleteAfterSend,
                        cancellationToken);

                    if (!advanced)
                    {
                        // Fencing lease check thất bại -> worker đã mất lease. Rollback và trả LeaseLost
                        await tx.RollbackAsync(CancellationToken.None);
                        itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ReminderItemProcessOutcome.LeaseLost));
                        return new ProcessOrderGroupResult(processed, noticesSent, completed, failed, itemResults);
                    }

                    noticesSent++;
                    if (eval.CompleteAfterSend)
                    {
                        completed++;
                    }
                    itemResults.Add(new ReminderItemProcessResult(desc.ScheduleId, desc.LeaseToken, ReminderItemProcessOutcome.Succeeded));
                }
            }

            // 6. Commit atomicity: outbox và cập nhật lịch được lưu cùng lúc
            await tx.CommitAsync(cancellationToken);
            return new ProcessOrderGroupResult(processed, noticesSent, completed, failed, itemResults);
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _logger.LogError(ex, "Lỗi xử lý nhóm lịch nhắc cho Order {OrderId}, đã rollback transaction.", orderId);
            failed += descriptors.Count;
            var retryableResults = descriptors.Select(d => new ReminderItemProcessResult(
                d.ScheduleId,
                d.LeaseToken,
                ReminderItemProcessOutcome.RetryableFailure,
                ex.Message)).ToList();
            return new ProcessOrderGroupResult(processed, 0, 0, failed, retryableResults);
        }
    }
}
