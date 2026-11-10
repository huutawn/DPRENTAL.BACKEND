using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.Policies;

public enum ReminderPolicyAction
{
    SendToday = 1,
    Reschedule = 2,
    WindowExpired = 3
}

public sealed record ReminderPolicyEvaluation(
    ReminderPolicyAction Action,
    string? ReminderType,
    DateOnly? ReminderDate,
    DateTimeOffset? ActionDeadline,
    DateTimeOffset? NextTriggerAt,
    bool IsOverdue,
    bool CompleteAfterSend);

public static class RentalReminderPolicy
{
    public static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    public static readonly TimeOnly ReminderTimeVn = new(8, 0, 0);

    public static DateOnly ToVietnamDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(VietnamOffset).DateTime);

    public static TimeOnly ToVietnamTime(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(instant.ToOffset(VietnamOffset).DateTime);

    public static DateTimeOffset VietnamAt8Am(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 8, 0, 0, VietnamOffset);

    public static DateTimeOffset GetNextVietnam8Am(DateTimeOffset now)
    {
        DateOnly todayVn = ToVietnamDate(now);
        DateTimeOffset today8Am = VietnamAt8Am(todayVn);
        return now < today8Am ? today8Am : VietnamAt8Am(todayVn.AddDays(1));
    }

    public static DateTimeOffset ActionDeadline(DateOnly targetDate)
    {
        DateOnly nextDay = targetDate.AddDays(1);
        return new DateTimeOffset(nextDay.Year, nextDay.Month, nextDay.Day, 0, 0, 0, VietnamOffset);
    }

    public static DateTimeOffset? CalculateInitialTriggerAt(
        RentalReminderScheduleType scheduleType,
        DateOnly targetDate,
        DateTimeOffset now)
    {
        var eval = Evaluate(scheduleType, targetDate, lastNotifiedDate: null, overdueDayCount: 0, now);
        return eval.Action switch
        {
            ReminderPolicyAction.SendToday => now,
            ReminderPolicyAction.Reschedule => eval.NextTriggerAt,
            _ => null
        };
    }

    public static ReminderPolicyEvaluation Evaluate(
        RentalReminderScheduleType scheduleType,
        DateOnly targetDate,
        DateOnly? lastNotifiedDate,
        int overdueDayCount,
        DateTimeOffset now)
    {
        DateOnly todayVn = ToVietnamDate(now);
        DateTimeOffset triggerTimeToday = VietnamAt8Am(todayVn);
        bool isPast8AmToday = now >= triggerTimeToday;
        int diff = todayVn.DayNumber - targetDate.DayNumber;

        // 1. Quá D+30 hoặc đã ghi nhận đủ 30 ngày quá hạn -> WindowExpired
        if (diff > 30 || overdueDayCount >= 30)
        {
            return new ReminderPolicyEvaluation(
                Action: ReminderPolicyAction.WindowExpired,
                ReminderType: null,
                ReminderDate: null,
                ActionDeadline: ActionDeadline(targetDate),
                NextTriggerAt: null,
                IsOverdue: true,
                CompleteAfterSend: false);
        }

        // 2. Ngày hôm nay đã gửi nhắc rồi -> Reschedule sang 08:00 ngày mai (hoặc WindowExpired nếu mai > D+30)
        if (lastNotifiedDate == todayVn)
        {
            DateOnly tomorrowVn = todayVn.AddDays(1);
            int tomorrowDiff = tomorrowVn.DayNumber - targetDate.DayNumber;
            if (tomorrowDiff > 30)
            {
                return new ReminderPolicyEvaluation(
                    Action: ReminderPolicyAction.WindowExpired,
                    ReminderType: null,
                    ReminderDate: null,
                    ActionDeadline: ActionDeadline(targetDate),
                    NextTriggerAt: null,
                    IsOverdue: diff > 0,
                    CompleteAfterSend: false);
            }

            return new ReminderPolicyEvaluation(
                Action: ReminderPolicyAction.Reschedule,
                ReminderType: null,
                ReminderDate: null,
                ActionDeadline: ActionDeadline(targetDate),
                NextTriggerAt: VietnamAt8Am(tomorrowVn),
                IsOverdue: diff > 0,
                CompleteAfterSend: false);
        }

        // 3. Sớm hơn D-3 -> Reschedule sang 08:00 ngày D-3
        if (diff < -3)
        {
            DateOnly firstEligible = targetDate.AddDays(-3);
            return new ReminderPolicyEvaluation(
                Action: ReminderPolicyAction.Reschedule,
                ReminderType: null,
                ReminderDate: null,
                ActionDeadline: ActionDeadline(targetDate),
                NextTriggerAt: VietnamAt8Am(firstEligible),
                IsOverdue: false,
                CompleteAfterSend: false);
        }

        // 4. Chưa đến 08:00 hôm nay -> Reschedule sang 08:00 hôm nay
        if (!isPast8AmToday)
        {
            return new ReminderPolicyEvaluation(
                Action: ReminderPolicyAction.Reschedule,
                ReminderType: null,
                ReminderDate: null,
                ActionDeadline: ActionDeadline(targetDate),
                NextTriggerAt: triggerTimeToday,
                IsOverdue: diff > 0,
                CompleteAfterSend: false);
        }

        // 5. Đã đến hoặc qua 08:00 hôm nay -> SendToday
        bool isOverdue = diff > 0;
        string reminderType = scheduleType switch
        {
            RentalReminderScheduleType.Handover => isOverdue ? "HandoverOverdue" : "HandoverReminder",
            RentalReminderScheduleType.Return => isOverdue ? "ReturnOverdue" : "ReturnReminder",
            _ => throw new ArgumentOutOfRangeException(nameof(scheduleType))
        };

        // Khi gửi ngày D+30 (hoặc overdueDayCount == 29):
        // Enqueue event trước, sau đó hoàn tất lịch khi commit thành công.
        bool isLastDay = diff == 30 || overdueDayCount == 29;
        DateTimeOffset? nextTriggerAfterSend = isLastDay ? null : VietnamAt8Am(todayVn.AddDays(1));

        return new ReminderPolicyEvaluation(
            Action: ReminderPolicyAction.SendToday,
            ReminderType: reminderType,
            ReminderDate: todayVn,
            ActionDeadline: ActionDeadline(targetDate),
            NextTriggerAt: nextTriggerAfterSend,
            IsOverdue: isOverdue,
            CompleteAfterSend: isLastDay);
    }
}
