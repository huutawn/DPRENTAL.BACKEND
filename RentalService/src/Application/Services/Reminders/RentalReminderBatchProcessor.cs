#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Reminders;

public sealed class RentalReminderBatchProcessor : IRentalReminderBatchProcessor
{
    private readonly RentalServiceDbContext _dbContext;
    private readonly IRentalReminderScheduleRepository _scheduleRepo;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalEventProducer _eventProducer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReminderBatchProcessor> _logger;
    private readonly IUnitOfWork _unitOfWork;

    public RentalReminderBatchProcessor(
        RentalServiceDbContext dbContext,
        IRentalReminderScheduleRepository scheduleRepo,
        IOrderLockManager lockManager,
        IRentalEventProducer eventProducer,
        TimeProvider timeProvider,
        IUnitOfWork? unitOfWork = null,
        ILogger<RentalReminderBatchProcessor>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _scheduleRepo = scheduleRepo ?? throw new ArgumentNullException(nameof(scheduleRepo));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _eventProducer = eventProducer ?? throw new ArgumentNullException(nameof(eventProducer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RentalReminderBatchProcessor>.Instance;
        _unitOfWork = unitOfWork ?? (IUnitOfWork)dbContext;
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

            // Đọc trạng thái hiện tại của schedule (LastNotifiedDate, OverdueDayCount)
            var scheduleStates = await _dbContext.RentalReminderSchedules
                .AsNoTracking()
                .Where(s => scheduleIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => new { s.LastNotifiedDate, s.OverdueDayCount, s.Status }, cancellationToken);

            // 4. Đọc projection tối thiểu của Order (AsNoTracking, không tải full aggregate)
            var order = await _scheduleRepo.GetOrderReminderProjectionAsync(orderId, cancellationToken);
            if (order == null || order.OrderStatus is RentalOrderStatus.Cancelled or RentalOrderStatus.Expired or RentalOrderStatus.Completed)
            {
                foreach (var desc in descriptors)
                {
                    await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                    completed++;
                    processed++;
                }
                await tx.CommitAsync(cancellationToken);
                return new ProcessOrderGroupResult(processed, noticesSent, completed, failed);
            }

            foreach (var desc in orderedDescriptors)
            {
                processed++;

                if (!scheduleStates.TryGetValue(desc.ScheduleId, out var state) || state.Status != RentalReminderScheduleStatus.Active)
                {
                    continue;
                }

                var item = order.Items.FirstOrDefault(i => i.ItemId == desc.ItemId);
                if (item == null)
                {
                    await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                    completed++;
                    continue;
                }

                DateOnly targetDate;

                if (desc.ScheduleType == RentalReminderScheduleType.Handover)
                {
                    if (item.Status != RentalItemStatus.AwaitingHandover)
                    {
                        await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                        completed++;
                        continue;
                    }

                    if (item.HandoverInProgress)
                    {
                        // Handover đang đối soát: hoãn lần xét tiếp 5 phút, không chỉ nhả lease
                        _logger.LogInformation("Item {ItemId} đang có bàn giao dở dang/đối soát, hoãn lịch nhắc 5 phút", item.ItemId);
                        await _scheduleRepo.ReleaseLeaseWithTokenAsync(desc.ScheduleId, desc.LeaseToken, now.AddMinutes(5), now, cancellationToken);
                        continue;
                    }

                    targetDate = item.ExpectedStartDate;
                }
                else // Return
                {
                    if (item.Status != RentalItemStatus.InRental)
                    {
                        await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                        completed++;
                        continue;
                    }

                    if (item.ActiveRemainingUnits <= 0)
                    {
                        // Máy đang chờ xác nhận trả: tạm dừng lịch nhắc (không gửi, nhả lease không dời trigger)
                        _logger.LogInformation("Item {ItemId} không còn máy cần trả (đang chờ xác nhận hoặc đã trả hết), tạm dừng lịch nhắc", item.ItemId);
                        await _scheduleRepo.ReleaseLeaseWithTokenAsync(desc.ScheduleId, desc.LeaseToken, null, now, cancellationToken);
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
                    await _scheduleRepo.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, now, cancellationToken);
                    completed++;
                    continue;
                }

                if (eval.Action == ReminderPolicyAction.Reschedule)
                {
                    await _scheduleRepo.ReleaseLeaseWithTokenAsync(desc.ScheduleId, desc.LeaseToken, eval.NextTriggerAt, now, cancellationToken);
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
                        item.ActiveRemainingUnits,
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
                        throw new InvalidOperationException($"Fencing lease check thất bại cho schedule {desc.ScheduleId}. Token không khớp hoặc lease đã hết hạn.");
                    }

                    noticesSent++;
                    if (eval.CompleteAfterSend)
                    {
                        completed++;
                    }
                }
            }

            // 6. Commit atomicity: outbox và cập nhật lịch được lưu cùng lúc
            await tx.CommitAsync(cancellationToken);
            return new ProcessOrderGroupResult(processed, noticesSent, completed, failed);
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
            return new ProcessOrderGroupResult(processed, 0, 0, failed);
        }
    }
}
