using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

public sealed record HandoverObservationDto(
    Guid AssignmentId,
    UnitCondition Condition,
    string? Note = null,
    IEnumerable<Guid>? MediaIds = null);

public sealed record ReturnObservationDto(
    Guid AssignmentId,
    UnitCondition Condition,
    string? Note = null,
    IEnumerable<Guid>? MediaIds = null);

public sealed record DeductionLineDto(
    Guid AssignmentId,
    decimal ProposedAmount,
    string Reason,
    IEnumerable<Guid>? MediaIds = null);

public sealed record InitialRentalUnitAssignmentDto(
    Guid Id,
    Guid AssetId,
    Guid AllocationId);

/// <summary>
/// Bản sao thông tin Listing tại thời điểm checkout. Không chứa AssetCode hoặc SerialNumber.
/// Mọi trường đều có thể trống: Inventory hiện chỉ trả tên, giá và ảnh; đơn cũ vẫn giữ dữ liệu đã chụp.
/// </summary>
public sealed record RentalItemListingSnapshotDto(
    Guid? EquipmentTypeId,
    string? EquipmentTypeName,
    Guid? BrandId,
    string? BrandName,
    string? Model,
    int? SpecSchemaVersion,
    string? SpecValuesJson,
    Guid? ThumbnailMediaId)
{
    /// <summary>Snapshot chỉ có ảnh, đúng với những gì Inventory trả hiện nay.</summary>
    public static RentalItemListingSnapshotDto ThumbnailOnly(Guid? thumbnailMediaId) =>
        new(null, null, null, null, null, null, null, thumbnailMediaId);
}

public sealed record InitialRentalItemDto(
    Guid Id,
    Guid EquipmentListingId,
    Guid SupplierId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    string ListingTitleSnapshot,
    string? ListingDescriptionSnapshot,
    string? PickupLocationSnapshot,
    decimal DailyRateSnapshot,
    decimal DepositPerUnitSnapshot,
    string AllocationGroupId,
    IEnumerable<InitialRentalUnitAssignmentDto> Assignments,
    RentalItemListingSnapshotDto ListingSnapshot);

public sealed record RentalOperationCommand(
    int Version,
    string Action,
    Guid RentalOrderId,
    Guid? TargetItemId,
    string OperationKey,
    string? TargetGroupOrReference,
    Guid? ActorId,
    string? ProviderRequestJson);
