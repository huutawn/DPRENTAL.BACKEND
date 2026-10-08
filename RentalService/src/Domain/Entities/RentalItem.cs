using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_items", Schema = "rental")]
[Index(nameof(RentalOrderId))]
[Index(nameof(SupplierId))]
[Index(nameof(Status))]
[Index(nameof(SupplierConfirmationDeadline))]
[Index(nameof(DeductionDeadline))]
public class RentalItem
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public Guid EquipmentListingId { get; private set; }

    public Guid SupplierId { get; private set; }

    public int Quantity { get; private set; }

    public DateOnly ExpectedStartDate { get; private set; }

    public DateOnly ExpectedEndDate { get; private set; }

    [Required]
    [MaxLength(200)]
    public string ListingTitleSnapshot { get; private set; } = string.Empty;

    [MaxLength(2000)]
    public string? ListingDescriptionSnapshot { get; private set; }

    [Required]
    [MaxLength(500)]
    public string PickupLocationSnapshot { get; private set; } = string.Empty;

    [Precision(18, 2)]
    public decimal DailyRateSnapshot { get; private set; }

    [Precision(18, 2)]
    public decimal DepositPerUnitSnapshot { get; private set; }

    [Precision(18, 2)]
    public decimal RentalAmount { get; private set; }

    [Precision(18, 2)]
    public decimal DepositAmount { get; private set; }

    public RentalItemStatus Status { get; private set; }

    [Required]
    [MaxLength(64)]
    public string AllocationGroupId { get; private set; } = string.Empty;

    public DateTimeOffset? SupplierConfirmationDeadline { get; private set; }

    public DateTimeOffset? SupplierDecisionAt { get; private set; }

    [MaxLength(2000)]
    public string? SupplierDecisionReason { get; private set; }

    public DateTimeOffset? ReturnsCompletedAt { get; private set; }

    public DateTimeOffset? DeductionDeadline { get; private set; }

    [Precision(18, 2)]
    public decimal? FinalDeductionAmount { get; private set; }

    [MaxLength(256)]
    public string? RefundRef { get; private set; }

    [MaxLength(256)]
    public string? SettlementRef { get; private set; }

    public DateTimeOffset? InventoryClosureConfirmedAt { get; private set; }

    public DateTimeOffset? FinancialClosureConfirmedAt { get; private set; }

    [NotMapped]
    public int RentalDays => ExpectedEndDate.DayNumber - ExpectedStartDate.DayNumber;

    [NotMapped]
    public bool AreAllUnitsReturned =>
        _unitAssignments.Count > 0 &&
        _unitAssignments.All(u => u.Status is RentalUnitAssignmentStatus.ReturnedNormal or RentalUnitAssignmentStatus.ReturnedDamaged);

    [NotMapped]
    public bool HasDamagedUnits =>
        _unitAssignments.Any(u => u.Status == RentalUnitAssignmentStatus.ReturnedDamaged);

    [ForeignKey(nameof(RentalOrderId))]
    public RentalOrder? RentalOrder { get; private set; }

    private readonly List<RentalUnitAssignment> _unitAssignments = [];
    public IReadOnlyCollection<RentalUnitAssignment> UnitAssignments => _unitAssignments.AsReadOnly();

    public HandoverRecord? HandoverRecord { get; private set; }

    private readonly List<ReturnRecord> _returnRecords = [];
    public IReadOnlyCollection<ReturnRecord> ReturnRecords => _returnRecords.AsReadOnly();

    public DeductionProposal? DeductionProposal { get; private set; }

    private RentalItem()
    {
    }

    internal RentalItem(
        Guid id,
        Guid rentalOrderId,
        Guid equipmentListingId,
        Guid supplierId,
        int quantity,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate,
        string listingTitleSnapshot,
        string? listingDescriptionSnapshot,
        string pickupLocationSnapshot,
        decimal dailyRateSnapshot,
        decimal depositPerUnitSnapshot,
        string allocationGroupId,
        IEnumerable<InitialRentalUnitAssignmentDto> assignments)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        EquipmentListingId = equipmentListingId;
        SupplierId = supplierId;
        Quantity = quantity;
        ExpectedStartDate = expectedStartDate;
        ExpectedEndDate = expectedEndDate;
        ListingTitleSnapshot = listingTitleSnapshot;
        ListingDescriptionSnapshot = listingDescriptionSnapshot;
        PickupLocationSnapshot = pickupLocationSnapshot;
        DailyRateSnapshot = dailyRateSnapshot;
        DepositPerUnitSnapshot = depositPerUnitSnapshot;
        AllocationGroupId = allocationGroupId;
        Status = RentalItemStatus.PendingPayment;

        int rentalDays = expectedEndDate.DayNumber - expectedStartDate.DayNumber;
        RentalAmount = dailyRateSnapshot * quantity * rentalDays;
        DepositAmount = depositPerUnitSnapshot * quantity;

        foreach (var a in assignments)
        {
            _unitAssignments.Add(new RentalUnitAssignment(a.Id, Id, a.AssetId, a.AllocationId));
        }
    }

    internal void SetPaymentConfirmed(DateTimeOffset confirmationDeadline)
    {
        Status = RentalItemStatus.PendingSupplierConfirmation;
        SupplierConfirmationDeadline = confirmationDeadline;
    }

    internal void SetPaymentExpired()
    {
        Status = RentalItemStatus.Expired;
        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }
    }

    internal void SetCancelled()
    {
        Status = RentalItemStatus.Cancelled;
        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }
    }

    internal Result SupplierAccept(Guid supplierId, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        if (SupplierConfirmationDeadline.HasValue && utcNow > SupplierConfirmationDeadline.Value)
        {
            return RentalErrors.SupplierDeadlineExpired;
        }

        Status = RentalItemStatus.AwaitingHandover;
        SupplierDecisionAt = utcNow;
        return Result.Success();
    }

    internal Result SupplierReject(Guid supplierId, string reason, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        Status = RentalItemStatus.Rejected;
        SupplierDecisionAt = utcNow;
        SupplierDecisionReason = reason;

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result SupplierTimeout(DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return RentalErrors.InvalidItemStatus;
        }

        Status = RentalItemStatus.Expired;
        SupplierDecisionAt = utcNow;
        SupplierDecisionReason = "Supplier không xác nhận đúng hạn.";

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result CancelBeforeHandover(string reason, DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.InvalidItemStatus;
        }

        Status = RentalItemStatus.Cancelled;
        SupplierDecisionReason = reason;

        foreach (var u in _unitAssignments)
        {
            u.Release(null);
        }

        return Result.Success();
    }

    internal Result SubmitHandover(
        Guid handoverRecordId,
        Guid supplierId,
        DateTimeOffset actualHandoverAt,
        IEnumerable<HandoverObservationDto> observations,
        string? note,
        DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.InvalidHandoverActor;
        }

        if (HandoverRecord is not null)
        {
            return RentalErrors.HandoverAlreadySubmitted;
        }

        var obsList = observations.ToList();
        if (obsList.Count != _unitAssignments.Count)
        {
            return RentalErrors.IncompleteHandover;
        }

        Dictionary<Guid, RentalUnitAssignment> assignDict = _unitAssignments.ToDictionary(u => u.Id);
        foreach (var obs in obsList)
        {
            if (!assignDict.TryGetValue(obs.AssignmentId, out RentalUnitAssignment? unit))
            {
                return RentalErrors.AssignmentNotFound;
            }

            if (unit.Status != RentalUnitAssignmentStatus.Assigned)
            {
                return RentalErrors.InvalidAssignmentStatus;
            }
        }

        HandoverRecord = new HandoverRecord(
            handoverRecordId,
            Id,
            actualHandoverAt,
            note,
            supplierId,
            utcNow,
            utcNow);

        foreach (var obs in obsList)
        {
            RentalUnitAssignment unit = assignDict[obs.AssignmentId];
            unit.RecordHandover(handoverRecordId, obs.Condition, obs.Note, obs.MediaIds);
            HandoverRecord.AddAssignment(unit);
        }

        Status = RentalItemStatus.InRental;
        return Result.Success();
    }

    internal Result AcknowledgeHandover(Guid renterId, DateTimeOffset acknowledgedAt, string? note)
    {
        if (HandoverRecord is null)
        {
            return RentalErrors.InvalidItemStatus;
        }

        HandoverRecord.Acknowledge(renterId, acknowledgedAt, note);
        return Result.Success();
    }

    internal Result SubmitReturn(
        Guid returnRecordId,
        Guid supplierId,
        DateTimeOffset actualReturnAt,
        IEnumerable<ReturnObservationDto> observations,
        string? note,
        DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.InRental)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.InvalidReturnActor;
        }

        var obsList = observations.ToList();
        if (obsList.Count == 0)
        {
            return RentalErrors.IncompleteReturn;
        }

        Dictionary<Guid, RentalUnitAssignment> assignDict = _unitAssignments.ToDictionary(u => u.Id);
        foreach (var obs in obsList)
        {
            if (!assignDict.TryGetValue(obs.AssignmentId, out RentalUnitAssignment? unit))
            {
                return RentalErrors.AssignmentNotFound;
            }

            if (unit.Status != RentalUnitAssignmentStatus.HandedOver)
            {
                return RentalErrors.UnitNotHandedOver;
            }
        }

        ReturnRecord returnRecord = new(
            returnRecordId,
            Id,
            actualReturnAt,
            note,
            supplierId,
            utcNow);

        _returnRecords.Add(returnRecord);

        foreach (var obs in obsList)
        {
            RentalUnitAssignment unit = assignDict[obs.AssignmentId];
            unit.RecordReturn(returnRecordId, obs.Condition, obs.Note, obs.MediaIds, utcNow, null);
            returnRecord.AddAssignment(unit);
        }

        if (AreAllUnitsReturned)
        {
            ReturnsCompletedAt = utcNow;
            if (HasDamagedUnits)
            {
                Status = RentalItemStatus.PendingSettlement;
                DeductionDeadline = utcNow.AddHours(48);
            }
        }

        return Result.Success();
    }

    internal Result SubmitDeductionProposal(
        Guid proposalId,
        Guid supplierId,
        IEnumerable<DeductionLineDto> lines,
        DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSettlement)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        if (DeductionProposal is not null)
        {
            return RentalErrors.DeductionAlreadySubmitted;
        }

        if (DeductionDeadline.HasValue && utcNow > DeductionDeadline.Value)
        {
            return RentalErrors.DeductionDeadlineExpired;
        }

        var lineList = lines.ToList();
        if (lineList.Count == 0)
        {
            return RentalErrors.InvalidSnapshot;
        }

        decimal totalAmount = lineList.Sum(l => l.ProposedAmount);
        if (totalAmount < 0 || totalAmount > DepositAmount)
        {
            return RentalErrors.DeductionAmountExceedsDeposit;
        }

        Dictionary<Guid, RentalUnitAssignment> assignDict = _unitAssignments.ToDictionary(u => u.Id);
        foreach (var line in lineList)
        {
            if (!assignDict.TryGetValue(line.AssignmentId, out RentalUnitAssignment? unit))
            {
                return RentalErrors.AssignmentNotFound;
            }

            if (unit.Status != RentalUnitAssignmentStatus.ReturnedDamaged)
            {
                return RentalErrors.InvalidAssignmentStatus;
            }
        }

        DeductionProposal proposal = new(
            proposalId,
            Id,
            supplierId,
            utcNow,
            utcNow.AddHours(48));

        foreach (var line in lineList)
        {
            RentalUnitAssignment unit = assignDict[line.AssignmentId];
            unit.RecordDeduction(proposalId, line.ProposedAmount, line.Reason, line.MediaIds);
            proposal.AddAssignment(unit);
        }

        DeductionProposal = proposal;
        return Result.Success();
    }

    internal Result ExpireDeductionSubmission(DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSettlement)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (DeductionProposal is not null)
        {
            return RentalErrors.DeductionAlreadySubmitted;
        }

        FinalDeductionAmount = 0m;
        return Result.Success();
    }

    internal Result AcceptDeductionProposal(Guid renterId, DateTimeOffset utcNow)
    {
        if (DeductionProposal is null)
        {
            return RentalErrors.ProposalNotFound;
        }

        if (DeductionProposal.Status != DeductionProposalStatus.Submitted)
        {
            return RentalErrors.ProposalNotSubmitted;
        }

        DeductionProposal.Accept(renterId, utcNow);
        FinalDeductionAmount = DeductionProposal.TotalProposedAmount;
        return Result.Success();
    }

    internal Result TimeoutDeductionProposal(DateTimeOffset utcNow)
    {
        if (DeductionProposal is null)
        {
            return RentalErrors.ProposalNotFound;
        }

        if (DeductionProposal.Status != DeductionProposalStatus.Submitted)
        {
            return RentalErrors.ProposalNotSubmitted;
        }

        DeductionProposal.AcceptByTimeout(utcNow);
        FinalDeductionAmount = DeductionProposal.TotalProposedAmount;
        return Result.Success();
    }

    internal Result DisputeDeductionProposal(
        Guid disputeId,
        Guid renterId,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow)
    {
        if (DeductionProposal is null)
        {
            return RentalErrors.ProposalNotFound;
        }

        if (DeductionProposal.Status != DeductionProposalStatus.Submitted)
        {
            return RentalErrors.ProposalNotSubmitted;
        }

        DeductionProposal.OpenDispute(disputeId, renterId, reason, mediaIds, utcNow);
        return Result.Success();
    }

    internal Result ResolveDispute(
        Guid adminId,
        decimal finalDeductionAmount,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow)
    {
        if (DeductionProposal?.Dispute is null)
        {
            return RentalErrors.DisputeNotFound;
        }

        if (DeductionProposal.Dispute.Status != DisputeStatus.Open)
        {
            return RentalErrors.DisputeNotOpen;
        }

        if (finalDeductionAmount < 0)
        {
            return RentalErrors.NegativeAmount;
        }

        if (finalDeductionAmount > DeductionProposal.TotalProposedAmount)
        {
            return RentalErrors.ResolutionExceedsProposedAmount;
        }

        if (finalDeductionAmount > DepositAmount)
        {
            return RentalErrors.ResolutionExceedsDeposit;
        }

        DeductionProposal.Dispute.Resolve(finalDeductionAmount, reason, adminId, utcNow, mediaIds);
        DeductionProposal.ApplyResolution();
        FinalDeductionAmount = finalDeductionAmount;
        return Result.Success();
    }

    internal Result ConfirmClosuresAndComplete(
        DateTimeOffset inventoryClosureAt,
        DateTimeOffset financialClosureAt,
        string? settlementRef,
        string? refundRef)
    {
        if (!AreAllUnitsReturned)
        {
            return RentalErrors.IncompleteReturn;
        }

        if (DeductionProposal?.Dispute?.Status == DisputeStatus.Open)
        {
            return RentalErrors.DisputeBlocksCompletion;
        }

        if (!HasDamagedUnits)
        {
            FinalDeductionAmount = 0m;
        }

        InventoryClosureConfirmedAt = inventoryClosureAt;
        FinancialClosureConfirmedAt = financialClosureAt;
        SettlementRef = settlementRef;
        RefundRef = refundRef;
        Status = RentalItemStatus.Completed;
        return Result.Success();
    }
}
