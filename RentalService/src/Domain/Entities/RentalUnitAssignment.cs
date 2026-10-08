using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_unit_assignments", Schema = "rental")]
[Index(nameof(RentalItemId), nameof(AssetId), IsUnique = true)]
[Index(nameof(RentalItemId), nameof(AllocationId), IsUnique = true)]
public class RentalUnitAssignment
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalItemId { get; private set; }

    public Guid AssetId { get; private set; }

    public Guid AllocationId { get; private set; }

    public RentalUnitAssignmentStatus Status { get; private set; }

    public Guid? HandoverRecordId { get; private set; }

    public UnitCondition? HandoverCondition { get; private set; }

    [MaxLength(2000)]
    public string? HandoverNote { get; private set; }

    public List<Guid> HandoverMediaIds { get; private set; } = [];

    public Guid? ReturnRecordId { get; private set; }

    public UnitCondition? ReturnCondition { get; private set; }

    [MaxLength(2000)]
    public string? ReturnNote { get; private set; }

    public List<Guid> ReturnMediaIds { get; private set; } = [];

    public DateTimeOffset? AllocationClosedAt { get; private set; }

    [MaxLength(256)]
    public string? InventoryDispositionRef { get; private set; }

    public Guid? DeductionProposalId { get; private set; }

    [Precision(18, 2)]
    public decimal? ProposedDeductionAmount { get; private set; }

    [MaxLength(2000)]
    public string? DeductionReason { get; private set; }

    public List<Guid> DeductionMediaIds { get; private set; } = [];

    [ForeignKey(nameof(RentalItemId))]
    public RentalItem? RentalItem { get; private set; }

    [ForeignKey(nameof(HandoverRecordId))]
    public HandoverRecord? HandoverRecord { get; private set; }

    [ForeignKey(nameof(ReturnRecordId))]
    public ReturnRecord? ReturnRecord { get; private set; }

    [ForeignKey(nameof(DeductionProposalId))]
    public DeductionProposal? DeductionProposal { get; private set; }

    private RentalUnitAssignment()
    {
    }

    internal RentalUnitAssignment(
        Guid id,
        Guid rentalItemId,
        Guid assetId,
        Guid allocationId)
    {
        Id = id;
        RentalItemId = rentalItemId;
        AssetId = assetId;
        AllocationId = allocationId;
        Status = RentalUnitAssignmentStatus.Assigned;
    }

    internal void RecordHandover(
        Guid handoverRecordId,
        UnitCondition condition,
        string? note,
        IEnumerable<Guid>? mediaIds)
    {
        HandoverRecordId = handoverRecordId;
        HandoverCondition = condition;
        HandoverNote = note;
        HandoverMediaIds = (mediaIds ?? []).ToList();
        Status = RentalUnitAssignmentStatus.HandedOver;
    }

    internal void RecordReturn(
        Guid returnRecordId,
        UnitCondition condition,
        string? note,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset? allocationClosedAt,
        string? dispositionRef)
    {
        ReturnRecordId = returnRecordId;
        ReturnCondition = condition;
        ReturnNote = note;
        ReturnMediaIds = (mediaIds ?? []).ToList();
        AllocationClosedAt = allocationClosedAt;
        InventoryDispositionRef = dispositionRef;
        Status = condition == UnitCondition.Damaged
            ? RentalUnitAssignmentStatus.ReturnedDamaged
            : RentalUnitAssignmentStatus.ReturnedNormal;
    }

    internal void RecordDeduction(
        Guid deductionProposalId,
        decimal amount,
        string reason,
        IEnumerable<Guid>? mediaIds)
    {
        DeductionProposalId = deductionProposalId;
        ProposedDeductionAmount = amount;
        DeductionReason = reason;
        DeductionMediaIds = (mediaIds ?? []).ToList();
    }

    internal void Release(DateTimeOffset? allocationClosedAt)
    {
        Status = RentalUnitAssignmentStatus.Released;
        AllocationClosedAt = allocationClosedAt;
    }
}
