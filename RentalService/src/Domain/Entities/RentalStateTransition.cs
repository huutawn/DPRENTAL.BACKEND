using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_state_transitions", Schema = "rental")]
[Index(nameof(RentalOrderId))]
[Index(nameof(RentalItemId))]
[Index(nameof(OccurredAt))]
public class RentalStateTransition
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public Guid? RentalItemId { get; private set; }

    [MaxLength(64)]
    public string? FromState { get; private set; }

    [Required]
    [MaxLength(64)]
    public string ToState { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Action { get; private set; } = string.Empty;

    public Guid? ActorId { get; private set; }

    public ActorRole ActorRole { get; private set; }

    [MaxLength(2000)]
    public string? Reason { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    [Required]
    [MaxLength(64)]
    public string OperationId { get; private set; } = string.Empty;

    [ForeignKey(nameof(RentalOrderId))]
    public RentalOrder? RentalOrder { get; private set; }

    [ForeignKey(nameof(RentalItemId))]
    public RentalItem? RentalItem { get; private set; }

    private RentalStateTransition()
    {
    }

    internal RentalStateTransition(
        Guid id,
        Guid rentalOrderId,
        Guid? rentalItemId,
        string? fromState,
        string toState,
        string action,
        Guid? actorId,
        ActorRole actorRole,
        string? reason,
        DateTimeOffset occurredAt,
        string operationId)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        RentalItemId = rentalItemId;
        FromState = fromState;
        ToState = toState;
        Action = action;
        ActorId = actorId;
        ActorRole = actorRole;
        Reason = reason;
        OccurredAt = occurredAt;
        OperationId = operationId;
    }
}
