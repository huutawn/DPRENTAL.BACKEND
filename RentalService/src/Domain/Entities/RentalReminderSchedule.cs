using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_reminder_schedules", Schema = "rental")]
public class RentalReminderSchedule
{
    private RentalReminderSchedule() { }

    public RentalReminderSchedule(
        Guid id,
        Guid rentalOrderId,
        Guid rentalItemId,
        RentalReminderScheduleType scheduleType,
        RentalReminderScheduleStatus status,
        int remainingUnitCount,
        DateTimeOffset? nextTriggerAt,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id cannot be empty.", nameof(id));
        if (rentalOrderId == Guid.Empty) throw new ArgumentException("RentalOrderId cannot be empty.", nameof(rentalOrderId));
        if (rentalItemId == Guid.Empty) throw new ArgumentException("RentalItemId cannot be empty.", nameof(rentalItemId));

        Id = id;
        RentalOrderId = rentalOrderId;
        RentalItemId = rentalItemId;
        ScheduleType = scheduleType;
        Status = status;
        RemainingUnitCount = Math.Max(0, remainingUnitCount);
        NextTriggerAt = nextTriggerAt;
        OverdueDayCount = 0;
        RetryCount = 0;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public Guid RentalItemId { get; private set; }

    public RentalReminderScheduleType ScheduleType { get; private set; }

    public RentalReminderScheduleStatus Status { get; private set; }

    public DateTimeOffset? NextTriggerAt { get; private set; }

    public DateOnly? LastNotifiedDate { get; private set; }

    public DateTimeOffset? LastNotifiedAt { get; private set; }

    public int OverdueDayCount { get; private set; }

    public int RemainingUnitCount { get; private set; }

    public DateTimeOffset? LeasedUntil { get; private set; }

    [MaxLength(64)]
    public string? LeasedBy { get; private set; }
    public Guid? LeaseToken { get; private set; }


    public int RetryCount { get; private set; }

    public DateTimeOffset? NextRetryAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RentalReminderSchedule Create(
        Guid orderId,
        Guid itemId,
        RentalReminderScheduleType type,
        int initialQuantity,
        DateTimeOffset? nextTriggerAt,
        DateTimeOffset now)
    {
        return new RentalReminderSchedule(
            Guid.NewGuid(),
            orderId,
            itemId,
            type,
            RentalReminderScheduleStatus.Active,
            initialQuantity,
            nextTriggerAt,
            now);
    }

    public void UpdateRemainingUnits(int units, DateTimeOffset? nextTriggerAt, DateTimeOffset now)
    {
        RemainingUnitCount = Math.Max(0, units);
        NextTriggerAt = nextTriggerAt;
        LeasedBy = null;
        LeaseToken = null;
        LeasedUntil = null;
        UpdatedAt = now;
    }

    public void Pause(DateTimeOffset now)
    {
        RemainingUnitCount = 0;
        NextTriggerAt = null;
        LeasedBy = null;
        LeaseToken = null;
        LeasedUntil = null;
        UpdatedAt = now;
    }

    public void AdvanceSchedule(
        DateOnly notifiedDate,
        DateTimeOffset firedAt,
        DateTimeOffset? nextTriggerAt,
        bool isOverdue,
        DateTimeOffset now)
    {
        LastNotifiedDate = notifiedDate;
        LastNotifiedAt = firedAt;
        NextTriggerAt = nextTriggerAt;
        RetryCount = 0;
        NextRetryAt = null;
        LeasedUntil = null;
        LeasedBy = null;
        LeaseToken = null;
        UpdatedAt = now;

        if (isOverdue)
        {
            OverdueDayCount++;
        }
    }

    public void Fail(DateTimeOffset nextRetryAt, DateTimeOffset now)
    {
        RetryCount++;
        NextRetryAt = nextRetryAt;
        LeasedUntil = null;
        LeasedBy = null;
        LeaseToken = null;
        UpdatedAt = now;
    }

    public void Claim(string leasedBy, Guid leaseToken, DateTimeOffset leaseUntil, DateTimeOffset now)
    {
        LeasedBy = leasedBy;
        LeaseToken = leaseToken;
        LeasedUntil = leaseUntil;
        UpdatedAt = now;
    }

    public void ReleaseLease(DateTimeOffset? nextTriggerAt, DateTimeOffset now)
    {
        LeasedBy = null;
        LeaseToken = null;
        LeasedUntil = null;
        NextTriggerAt = nextTriggerAt;
        UpdatedAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        Status = RentalReminderScheduleStatus.Completed;
        NextTriggerAt = null;
        LeasedUntil = null;
        LeasedBy = null;
        LeaseToken = null;
        UpdatedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = RentalReminderScheduleStatus.Cancelled;
        NextTriggerAt = null;
        LeasedUntil = null;
        LeasedBy = null;
        LeaseToken = null;
        UpdatedAt = now;
    }
}
