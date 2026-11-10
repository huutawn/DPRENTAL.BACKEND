#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Reminders;

public sealed class RentalReminderScheduleService : IRentalReminderScheduleService
{
    private readonly IRentalReminderScheduleRepository _schedules;
    private readonly ILogger<RentalReminderScheduleService> _logger;

    public RentalReminderScheduleService(
        IRentalReminderScheduleRepository schedules,
        ILogger<RentalReminderScheduleService> logger)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureHandoverScheduleCreatedAsync(
        Guid orderId,
        Guid itemId,
        DateOnly expectedStartDate,
        int quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var existing = await _schedules.GetByItemAndTypeAsync(itemId, RentalReminderScheduleType.Handover, cancellationToken);
        if (existing != null)
        {
            return;
        }

        var nextTrigger = RentalReminderPolicy.CalculateInitialTriggerAt(RentalReminderScheduleType.Handover, expectedStartDate, now);
        var schedule = RentalReminderSchedule.Create(orderId, itemId, RentalReminderScheduleType.Handover, quantity, nextTrigger, now);
        await _schedules.AddAsync(schedule, cancellationToken);
        _logger.LogInformation("Tạo lịch nhắc bàn giao cho Item {ItemId} trong Order {OrderId}, NextTrigger: {Trigger}",
            itemId, orderId, nextTrigger);
    }

    public async Task CompleteHandoverAndCreateReturnScheduleAsync(
        Guid orderId,
        Guid itemId,
        DateOnly expectedEndDate,
        int quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var handoverSchedule = await _schedules.GetByItemAndTypeAsync(itemId, RentalReminderScheduleType.Handover, cancellationToken);
        if (handoverSchedule is { Status: RentalReminderScheduleStatus.Active })
        {
            handoverSchedule.Complete(now);
            await _schedules.UpdateAsync(handoverSchedule, cancellationToken);
            _logger.LogInformation("Hoàn tất lịch nhắc bàn giao cho Item {ItemId}", itemId);
        }

        var returnSchedule = await _schedules.GetByItemAndTypeAsync(itemId, RentalReminderScheduleType.Return, cancellationToken);
        if (returnSchedule == null)
        {
            var nextTrigger = RentalReminderPolicy.CalculateInitialTriggerAt(RentalReminderScheduleType.Return, expectedEndDate, now);
            returnSchedule = RentalReminderSchedule.Create(orderId, itemId, RentalReminderScheduleType.Return, quantity, nextTrigger, now);
            await _schedules.AddAsync(returnSchedule, cancellationToken);
            _logger.LogInformation("Tạo lịch nhắc trả cho Item {ItemId} trong Order {OrderId}, NextTrigger: {Trigger}",
                itemId, orderId, nextTrigger);
        }
    }

    public async Task OnReturnUnitsChangedAsync(
        Guid itemId,
        int remainingActiveUnits,
        DateOnly expectedEndDate,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _schedules.GetByItemAndTypeAsync(itemId, RentalReminderScheduleType.Return, cancellationToken);
        if (schedule is { Status: RentalReminderScheduleStatus.Active })
        {
            if (remainingActiveUnits <= 0)
            {
                schedule.UpdateRemainingUnits(0, nextTriggerAt: null, now);
                _logger.LogInformation("Item {ItemId} không còn máy nào cần trả (đang chờ xác nhận hoặc đã trả hết), tạm dừng lịch nhắc trả", itemId);
            }
            else
            {
                var nextTrigger = RentalReminderPolicy.CalculateInitialTriggerAt(RentalReminderScheduleType.Return, expectedEndDate, now);
                schedule.UpdateRemainingUnits(remainingActiveUnits, nextTrigger, now);
                _logger.LogInformation("Cập nhật số máy cần trả cho Item {ItemId}: {Remaining}, NextTrigger: {Trigger}",
                    itemId, remainingActiveUnits, nextTrigger);
            }
            await _schedules.UpdateAsync(schedule, cancellationToken);
        }
    }

    public async Task StopSchedulesForItemAsync(
        Guid itemId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var schedules = await _schedules.GetByItemIdAsync(itemId, cancellationToken);
        foreach (var schedule in schedules.Where(s => s.Status == RentalReminderScheduleStatus.Active))
        {
            schedule.Cancel(now);
            await _schedules.UpdateAsync(schedule, cancellationToken);
            _logger.LogInformation("Đã hủy lịch nhắc {ScheduleType} của Item {ItemId}", schedule.ScheduleType, itemId);
        }
    }
}
