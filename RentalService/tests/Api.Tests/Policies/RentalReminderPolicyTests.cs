using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Policies;

public sealed class RentalReminderPolicyTests
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    [Fact]
    public void Evaluate_FarBeforeD3_SchedulesForDMinus3At8Am()
    {
        var targetDate = new DateOnly(2026, 11, 20);
        // Current date: 2026-11-10 at 10:00 VN (D-10)
        var now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 0,
            now);

        Assert.Equal(ReminderPolicyAction.Reschedule, result.Action);
        Assert.False(result.IsOverdue);
        // Next trigger should be D-3: 2026-11-17 at 08:00 VN
        var expectedNext = new DateTimeOffset(2026, 11, 17, 8, 0, 0, VnOffset);
        Assert.Equal(expectedNext, result.NextTriggerAt);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Evaluate_UpcomingMilestones_Before8Am_SchedulesForTodayAt8Am(int dayOffset)
    {
        var targetDate = new DateOnly(2026, 11, 20);
        var evalDate = targetDate.AddDays(dayOffset);
        // 07:30 VN
        var now = new DateTimeOffset(evalDate.Year, evalDate.Month, evalDate.Day, 7, 30, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 0,
            now);

        Assert.Equal(ReminderPolicyAction.Reschedule, result.Action);
        Assert.False(result.IsOverdue);
        var expectedNext = new DateTimeOffset(evalDate.Year, evalDate.Month, evalDate.Day, 8, 0, 0, VnOffset);
        Assert.Equal(expectedNext, result.NextTriggerAt);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Evaluate_UpcomingMilestones_After8Am_FiresToday(int dayOffset)
    {
        var targetDate = new DateOnly(2026, 11, 20);
        var evalDate = targetDate.AddDays(dayOffset);
        // 08:30 VN
        var now = new DateTimeOffset(evalDate.Year, evalDate.Month, evalDate.Day, 8, 30, 0, VnOffset);

        var resultHandover = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 0,
            now);

        Assert.Equal(ReminderPolicyAction.SendToday, resultHandover.Action);
        Assert.False(resultHandover.IsOverdue);
        Assert.Equal("HandoverReminder", resultHandover.ReminderType);
        Assert.Equal(evalDate, resultHandover.ReminderDate);
        var expectedTomorrow = new DateTimeOffset(evalDate.AddDays(1).Year, evalDate.AddDays(1).Month, evalDate.AddDays(1).Day, 8, 0, 0, VnOffset);
        Assert.Equal(expectedTomorrow, resultHandover.NextTriggerAt);

        // Same for Return
        var resultReturn = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Return,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 0,
            now);

        Assert.Equal(ReminderPolicyAction.SendToday, resultReturn.Action);
        Assert.Equal("ReturnReminder", resultReturn.ReminderType);
    }

    [Fact]
    public void Evaluate_AlreadyNotifiedToday_DoesNotFireAgain()
    {
        var targetDate = new DateOnly(2026, 11, 20);
        var today = new DateOnly(2026, 11, 18); // D-2
        var now = new DateTimeOffset(2026, 11, 18, 14, 0, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: today,
            overdueDayCount: 0,
            now);

        Assert.Equal(ReminderPolicyAction.Reschedule, result.Action);
        var expectedTomorrow = new DateTimeOffset(2026, 11, 19, 8, 0, 0, VnOffset);
        Assert.Equal(expectedTomorrow, result.NextTriggerAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(29)]
    public void Evaluate_OverdueStage_After8Am_FiresOverdueReminder(int overdueDays)
    {
        var targetDate = new DateOnly(2026, 11, 20);
        var evalDate = targetDate.AddDays(overdueDays);
        var now = new DateTimeOffset(evalDate.Year, evalDate.Month, evalDate.Day, 9, 0, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: overdueDays - 1,
            now);

        Assert.Equal(ReminderPolicyAction.SendToday, result.Action);
        Assert.True(result.IsOverdue);
        Assert.Equal("HandoverOverdue", result.ReminderType);
        Assert.False(result.CompleteAfterSend);

        var resultReturn = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Return,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: overdueDays - 1,
            now);

        Assert.Equal(ReminderPolicyAction.SendToday, resultReturn.Action);
        Assert.True(resultReturn.IsOverdue);
        Assert.Equal("ReturnOverdue", resultReturn.ReminderType);
    }

    [Fact]
    public void Evaluate_Day30Overdue_FiresAndCompletes()
    {
        var targetDate = new DateOnly(2026, 11, 20);
        var evalDate = targetDate.AddDays(30); // D+30
        var now = new DateTimeOffset(evalDate.Year, evalDate.Month, evalDate.Day, 10, 0, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Return,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 29,
            now);

        Assert.Equal(ReminderPolicyAction.SendToday, result.Action);
        Assert.True(result.IsOverdue);
        Assert.True(result.CompleteAfterSend);
        Assert.Null(result.NextTriggerAt);
    }

    [Fact]
    public void Evaluate_BeyondDay30_MarksCompletedWithoutFiring()
    {
        var targetDate = new DateOnly(2026, 11, 20);
        var evalDate = targetDate.AddDays(31); // D+31
        var now = new DateTimeOffset(evalDate.Year, evalDate.Month, evalDate.Day, 10, 0, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 30,
            now);

        Assert.Equal(ReminderPolicyAction.WindowExpired, result.Action);
        Assert.Null(result.NextTriggerAt);
    }

    [Fact]
    public void CalculateInitialTriggerAt_AcceptedOnDMinus1After8Am_EligibleImmediately()
    {
        var targetDate = new DateOnly(2026, 11, 20);
        // Accepted on 2026-11-19 at 15:00 VN
        var now = new DateTimeOffset(2026, 11, 19, 15, 0, 0, VnOffset);

        var triggerAt = RentalReminderPolicy.CalculateInitialTriggerAt(
            RentalReminderScheduleType.Handover,
            targetDate,
            now);

        Assert.NotNull(triggerAt);
        Assert.Equal(now, triggerAt.Value);
    }

    [Fact]
    public void DowntimeRecovery_SkipsMissedDays_OnlyFiresCurrentDay()
    {
        var targetDate = new DateOnly(2026, 11, 20);
        // Server was down on D-3 (17th), D-2 (18th), recovers on D-1 (19th) at 11:00 VN
        var now = new DateTimeOffset(2026, 11, 19, 11, 0, 0, VnOffset);

        var result = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Handover,
            targetDate,
            lastNotifiedDate: null,
            overdueDayCount: 0,
            now);

        Assert.Equal(ReminderPolicyAction.SendToday, result.Action);
        Assert.Equal(new DateOnly(2026, 11, 19), result.ReminderDate);
        Assert.Equal("HandoverReminder", result.ReminderType);
    }

    [Fact]
    public void NextTriggerAt_IsTheSameVietnam8AmInstant_ExpressedInUtc()
    {
        // Bàn giao 10:32 VN ngày 11/11, trả máy 15/11: hẹn nhắc 08:00 VN ngày 12/11.
        var now = new DateTimeOffset(2026, 11, 11, 10, 32, 0, VnOffset);

        DateTimeOffset? rescheduled = RentalReminderPolicy.CalculateInitialTriggerAt(
            RentalReminderScheduleType.Return, new DateOnly(2026, 11, 15), now);
        DateTimeOffset? afterSend = RentalReminderPolicy.Evaluate(
            RentalReminderScheduleType.Return, new DateOnly(2026, 11, 13), null, 0, now).NextTriggerAt;

        // Npgsql chỉ ghi được DateTimeOffset offset 0 vào cột timestamptz.
        Assert.Equal(TimeSpan.Zero, rescheduled!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero), rescheduled.Value);
        Assert.Equal(TimeSpan.Zero, afterSend!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 8, 0, 0, VnOffset), afterSend.Value);
    }
}
