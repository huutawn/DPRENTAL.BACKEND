using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Policies;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_items", Schema = "rental")]
[Index(nameof(RentalOrderId))]
[Index(nameof(SupplierId))]
[Index(nameof(Status))]
[Index(nameof(SupplierConfirmationDeadline))]
[Index(nameof(DeductionDeadline))]
public partial class RentalItem
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public Guid RentalSubOrderId { get; private set; }

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

    public const int MaxPickupLocationSnapshotLength = 1024;

    // Snapshot địa chỉ kho nhận máy tại thời điểm đặt hàng (bao gồm địa chỉ chi tiết, tỉnh/phường và tọa độ).
    [MaxLength(MaxPickupLocationSnapshotLength)]
    public string? PickupLocationSnapshot { get; private set; }

    public Guid? EquipmentTypeId { get; private set; }

    [MaxLength(200)]
    public string? EquipmentTypeNameSnapshot { get; private set; }

    public Guid? BrandId { get; private set; }

    [MaxLength(200)]
    public string? BrandNameSnapshot { get; private set; }

    [MaxLength(200)]
    public string? ModelSnapshot { get; private set; }

    public int? SpecSchemaVersionSnapshot { get; private set; }

    [Column(TypeName = "jsonb")]
    public string? SpecValuesSnapshot { get; private set; }

    public Guid? ThumbnailMediaIdSnapshot { get; private set; }

    [Precision(18, 2)]
    public decimal DailyRateSnapshot { get; private set; }

    [Precision(18, 2)]
    public decimal DepositPerUnitSnapshot { get; private set; }

    [Precision(18, 2)]
    public decimal RentalAmount { get; private set; }

    [Precision(18, 2)]
    public decimal DepositAmount { get; private set; }

    [NotMapped]
    public decimal TotalAmount => RentalAmount + DepositAmount;

    public RentalItemStatus Status { get; private set; }

    internal void SetStatus(RentalItemStatus status) => Status = status;

    [Required]
    [MaxLength(64)]
    public string AllocationGroupId { get; private set; } = string.Empty;

    public DateTimeOffset? SupplierConfirmationDeadline { get; private set; }

    public DateTimeOffset? SupplierDecisionAt { get; private set; }

    public Guid? SupplierDecisionBy { get; private set; }

    [MaxLength(2000)]
    public string? SupplierDecisionReason { get; private set; }

    [MaxLength(2000)]
    public string? SupplierDecisionNote { get; private set; }
    [MaxLength(256)]
    public string? InventoryConfirmationRef { get; private set; }

    public DateTimeOffset? ReturnsCompletedAt { get; private set; }

    public DateTimeOffset? DeductionDeadline { get; private set; }

    [Precision(18, 2)]
    public decimal? FinalDeductionAmount { get; private set; }

    [MaxLength(256)]
    public string? RefundRef { get; private set; }

    [MaxLength(256)]
    public string? SettlementRef { get; private set; }

    [MaxLength(256)]
    public string? InventoryClosureRef { get; private set; }

    public DateTimeOffset? InventoryClosureConfirmedAt { get; private set; }

    public DateTimeOffset? FinancialClosureConfirmedAt { get; private set; }

    [NotMapped]
    public int RentalDays => RentalPricingPolicy.CalculateRentalDays(ExpectedStartDate, ExpectedEndDate);

    [NotMapped]
    public bool AreAllUnitsReturned =>
        _unitAssignments.Count > 0 &&
        _unitAssignments.All(u => u.Status is RentalUnitAssignmentStatus.ReturnedNormal or RentalUnitAssignmentStatus.ReturnedDamaged);

    [NotMapped]
    public bool HasDamagedUnits =>
        _unitAssignments.Any(u => u.Status == RentalUnitAssignmentStatus.ReturnedDamaged);

    [ForeignKey(nameof(RentalOrderId))]
    public RentalOrder? RentalOrder { get; internal set; }

    [ForeignKey(nameof(RentalSubOrderId))]
    public RentalSubOrder? RentalSubOrder { get; private set; }

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
        Guid rentalSubOrderId,
        Guid equipmentListingId,
        Guid supplierId,
        int quantity,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate,
        string listingTitleSnapshot,
        string? listingDescriptionSnapshot,
        string? pickupLocationSnapshot,
        decimal dailyRateSnapshot,
        decimal depositPerUnitSnapshot,
        string allocationGroupId,
        IEnumerable<InitialRentalUnitAssignmentDto> assignments,
        RentalItemListingSnapshotDto listingSnapshot)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        RentalSubOrderId = rentalSubOrderId;
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

        EquipmentTypeId = listingSnapshot.EquipmentTypeId;
        EquipmentTypeNameSnapshot = listingSnapshot.EquipmentTypeName?.Trim();
        BrandId = listingSnapshot.BrandId;
        BrandNameSnapshot = listingSnapshot.BrandName?.Trim();
        ModelSnapshot = listingSnapshot.Model?.Trim();
        SpecSchemaVersionSnapshot = listingSnapshot.SpecSchemaVersion;
        SpecValuesSnapshot = listingSnapshot.SpecValuesJson?.Trim();
        ThumbnailMediaIdSnapshot = listingSnapshot.ThumbnailMediaId;

        RentalAmount = RentalPricingPolicy.CalculateRentalAmount(dailyRateSnapshot, quantity, RentalDays);
        DepositAmount = RentalPricingPolicy.CalculateDepositAmount(depositPerUnitSnapshot, quantity);

        foreach (var a in assignments)
        {
            _unitAssignments.Add(new RentalUnitAssignment(a.Id, Id, a.AssetId, a.AllocationId));
        }
    }

    public void SetEquipmentTypeId(Guid equipmentTypeId)
    {
        if (equipmentTypeId == Guid.Empty) throw new ArgumentException("EquipmentTypeId cannot be empty.", nameof(equipmentTypeId));
        EquipmentTypeId = equipmentTypeId;
    }

    public void SetHandoverRecord(HandoverRecord handoverRecord)
    {
        ArgumentNullException.ThrowIfNull(handoverRecord);
        HandoverRecord = handoverRecord;
    }

    public void AddReturnRecord(ReturnRecord returnRecord)
    {
        ArgumentNullException.ThrowIfNull(returnRecord);
        _returnRecords.Add(returnRecord);
    }

    public void SetDeductionProposal(DeductionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        DeductionProposal = proposal;
    }

    public void AddUnitAssignment(RentalUnitAssignment unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _unitAssignments.Add(unit);
    }

    public void ReassignAllocationGroup(string newAllocationGroupId, IEnumerable<InitialRentalUnitAssignmentDto> newAssignments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newAllocationGroupId);
        ArgumentNullException.ThrowIfNull(newAssignments);

        AllocationGroupId = newAllocationGroupId;
        var list = newAssignments.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (i < _unitAssignments.Count)
            {
                _unitAssignments[i].UpdateAllocation(list[i].AssetId, list[i].AllocationId);
            }
            else
            {
                _unitAssignments.Add(new RentalUnitAssignment(list[i].Id, Id, list[i].AssetId, list[i].AllocationId));
            }
        }
    }
    public bool ContainsEvidenceMedia(Guid mediaId)
    {
        if (mediaId == Guid.Empty) return false;

        foreach (var unit in _unitAssignments)
        {
            if (unit.HandoverMediaIds.Contains(mediaId) ||
                unit.ReturnMediaIds.Contains(mediaId) ||
                unit.DeductionMediaIds.Contains(mediaId))
            {
                return true;
            }
        }

        if (DeductionProposal?.Dispute is not null)
        {
            if (DeductionProposal.Dispute.MediaIds.Contains(mediaId) ||
                DeductionProposal.Dispute.ResolutionMediaIds.Contains(mediaId))
            {
                return true;
            }
        }

        return false;
    }

    public static DateTimeOffset CalculateSupplierConfirmationDeadline(DateTimeOffset paidAt, DateOnly expectedStartDate)
    {
        TimeSpan vnOffset = TimeSpan.FromHours(7);
        DateTimeOffset startOfDayVn = new(expectedStartDate.Year, expectedStartDate.Month, expectedStartDate.Day, 0, 0, 0, vnOffset);
        DateTimeOffset cutoffUtc = startOfDayVn.AddSeconds(-1).ToUniversalTime();
        DateTimeOffset plus24hUtc = paidAt.ToUniversalTime().AddHours(24);

        return plus24hUtc < cutoffUtc ? plus24hUtc : cutoffUtc;
    }

    /// <summary>
    /// Thời gian hold còn sống sau hạn xác nhận, để worker chốt nốt các mục supplier đã bấm nhận trước hạn.
    /// </summary>
    public static readonly TimeSpan ConfirmationGracePeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hold được gia hạn tới hạn xác nhận cộng <see cref="ConfirmationGracePeriod"/>,
    /// nhưng phải hết trước 00:00 ngày giao theo giờ Việt Nam như luật của Inventory.
    /// </summary>
    public static DateTimeOffset CalculateConfirmationHoldExpiry(DateTimeOffset supplierConfirmationDeadline, DateOnly expectedStartDate)
    {
        TimeSpan vnOffset = TimeSpan.FromHours(7);
        DateTimeOffset startOfDayVn = new(expectedStartDate.Year, expectedStartDate.Month, expectedStartDate.Day, 0, 0, 0, vnOffset);
        DateTimeOffset lastSecondBeforeStartUtc = startOfDayVn.AddSeconds(-1).ToUniversalTime();
        DateTimeOffset withGraceUtc = supplierConfirmationDeadline.ToUniversalTime() + ConfirmationGracePeriod;

        return withGraceUtc < lastSecondBeforeStartUtc ? withGraceUtc : lastSecondBeforeStartUtc;
    }

    /// <param name="supplierConfirmationDeadline">Hạn xác nhận chung của cả đơn con chứa dòng này.</param>
    internal void RecordPaid(DateTimeOffset supplierConfirmationDeadline)
    {
        if (!SupplierConfirmationDeadline.HasValue)
        {
            SupplierConfirmationDeadline = supplierConfirmationDeadline;
        }
        Status = RentalItemStatus.PendingSupplierConfirmation;
    }

    internal void ApplySupplierAccept(
        Guid supplierId,
        DateTimeOffset decisionAt,
        string inventoryConfirmationRef,
        string operationId)
    {
        Status = RentalItemStatus.AwaitingHandover;
        SupplierDecisionBy = supplierId;
        SupplierDecisionAt = decisionAt;
        InventoryConfirmationRef = inventoryConfirmationRef;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            RentalItemStatus.PendingSupplierConfirmation.ToString(),
            RentalItemStatus.AwaitingHandover.ToString(),
            "SupplierAccept",
            supplierId,
            ActorRole.Supplier,
            "Supplier accepted rental item",
            decisionAt,
            operationId));
    }

    internal void ApplyHandover(DateTimeOffset utcNow, string operationKey)
    {
        // Kết quả trùng từ Inventory hoặc dòng đã đi tiếp thì không ghi đè trạng thái, không ghi thêm lịch sử.
        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return;
        }

        string from = Status.ToString();
        Status = RentalItemStatus.InRental;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            from,
            RentalItemStatus.InRental.ToString(),
            "MarkAssetsHandedOver",
            SupplierId,
            ActorRole.Supplier,
            "Supplier completed handover of rental item",
            utcNow,
            operationKey));
    }

    internal void ApplySupplierReject(
        Guid supplierId,
        DateTimeOffset decisionAt,
        string reason,
        string? note,
        string operationId)
    {
        Status = RentalItemStatus.Rejected;
        SupplierDecisionBy = supplierId;
        SupplierDecisionAt = decisionAt;
        SupplierDecisionReason = reason;
        SupplierDecisionNote = note;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            RentalItemStatus.PendingSupplierConfirmation.ToString(),
            RentalItemStatus.Rejected.ToString(),
            "SupplierReject",
            supplierId,
            ActorRole.Supplier,
            reason,
            decisionAt,
            operationId));
    }

    internal void ApplySupplierTimeout(DateTimeOffset expiredAt, string operationId)
    {
        string from = Status.ToString();
        Status = RentalItemStatus.Expired;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            from,
            RentalItemStatus.Expired.ToString(),
            "SupplierTimeout",
            null,
            ActorRole.System,
            "Supplier confirmation deadline expired without decision",
            expiredAt,
            operationId));
    }

    internal void ApplyHoldExpired(DateTimeOffset expiredAt, string operationId, string reason)
    {
        string from = Status.ToString();
        Status = RentalItemStatus.Expired;

        foreach (var unit in _unitAssignments)
        {
            unit.Release(expiredAt);
        }

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            from,
            RentalItemStatus.Expired.ToString(),
            "HoldExpired",
            null,
            ActorRole.System,
            reason,
            expiredAt,
            operationId));
    }

    internal void ApplyCancelled(
        DateTimeOffset cancelledAt,
        string operationId,
        string reason,
        Guid? actorId = null,
        ActorRole actorRole = ActorRole.System)
    {
        string from = Status.ToString();
        Status = RentalItemStatus.Cancelled;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            from,
            RentalItemStatus.Cancelled.ToString(),
            "Cancellation",
            actorId,
            actorRole,
            reason,
            cancelledAt,
            operationId));
    }

    internal bool MarkAssignmentsReleased(DateTimeOffset releasedAt)
    {
        bool changed = false;
        foreach (var unit in _unitAssignments)
        {
            if (unit.Status != RentalUnitAssignmentStatus.Released)
            {
                unit.Release(releasedAt);
                changed = true;
            }
        }
        return changed;
    }

    internal void RecordRefund(string refundRef)
    {
        RefundRef = refundRef;
    }
}
