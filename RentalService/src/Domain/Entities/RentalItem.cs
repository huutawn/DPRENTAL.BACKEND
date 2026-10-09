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

    [MaxLength(500)]
    public string? PickupLocationSnapshot { get; private set; }

    public Guid EquipmentTypeId { get; private set; }

    [Required]
    [MaxLength(200)]
    public string EquipmentTypeNameSnapshot { get; private set; } = string.Empty;

    public Guid BrandId { get; private set; }

    [Required]
    [MaxLength(200)]
    public string BrandNameSnapshot { get; private set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ModelSnapshot { get; private set; } = string.Empty;

    public int SpecSchemaVersionSnapshot { get; private set; }

    [Required]
    [Column(TypeName = "jsonb")]
    public string SpecValuesSnapshot { get; private set; } = "{}";

    public Guid? ThumbnailMediaIdSnapshot { get; private set; }

    public int RentalDays { get; private set; }

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
        string? pickupLocationSnapshot,
        decimal dailyRateSnapshot,
        decimal depositPerUnitSnapshot,
        string allocationGroupId,
        IEnumerable<InitialRentalUnitAssignmentDto> assignments,
        RentalItemListingSnapshotDto listingSnapshot)
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

        EquipmentTypeId = listingSnapshot.EquipmentTypeId;
        EquipmentTypeNameSnapshot = listingSnapshot.EquipmentTypeName.Trim();
        BrandId = listingSnapshot.BrandId;
        BrandNameSnapshot = listingSnapshot.BrandName.Trim();
        ModelSnapshot = listingSnapshot.Model.Trim();
        SpecSchemaVersionSnapshot = listingSnapshot.SpecSchemaVersion;
        SpecValuesSnapshot = listingSnapshot.SpecValuesJson.Trim();
        ThumbnailMediaIdSnapshot = listingSnapshot.ThumbnailMediaId;

        RentalDays = RentalPricingPolicy.CalculateRentalDays(expectedStartDate, expectedEndDate);
        RentalAmount = RentalPricingPolicy.CalculateRentalAmount(dailyRateSnapshot, quantity, RentalDays);
        DepositAmount = RentalPricingPolicy.CalculateDepositAmount(depositPerUnitSnapshot, quantity);

        foreach (var a in assignments)
        {
            _unitAssignments.Add(new RentalUnitAssignment(a.Id, Id, a.AssetId, a.AllocationId));
        }
    }
}
