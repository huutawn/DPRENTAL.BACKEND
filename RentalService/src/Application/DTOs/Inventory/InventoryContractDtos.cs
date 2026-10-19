using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Inventory;

// 1. GetListingRentalSnapshot
// Snapshot thương mại do Inventory cung cấp tại thời điểm checkout.
public sealed record ListingWarehouseDto(
    string? AddressDetail,
    string? ProvinceCode,
    string? ProvinceName,
    string? WardCode,
    string? WardName,
    double? Latitude,
    double? Longitude);

public sealed record ListingRentalSnapshotDto(
    Guid Id,
    Guid OwnerUserId,
    string Name,
    Guid? ThumbnailMediaId,
    decimal DailyRate,
    string Currency,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ListingWarehouseDto? Warehouse = null,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    string? Model = null,
    decimal? DepositPerUnit = null,
    int? SpecSchemaVersion = null,
    JsonElement? SpecValues = null,
    string? EquipmentTypeName = null,
    string? BrandName = null);

// 2. CheckListingAvailability: POST /equipment-listings/{listingId}/availability/check, đúng dạng Inventory.
// EndDate là ngày dùng máy cuối cùng (có tính): thuê 18, 19, 20 thì gửi startDate 18, endDate 20.
// ListingId nằm trên đường dẫn, không gửi trong body.
public sealed record ListingAvailabilityCheckRequest(
    [property: JsonIgnore] Guid ListingId,
    DateOnly StartDate,
    DateOnly EndDate,
    int RequestedQuantity);

public sealed record ListingAvailabilityCheckResponse(
    Guid ListingId,
    DateOnly StartDate,
    DateOnly EndDate,
    int RequestedQuantity,
    int AvailableQuantity,
    bool IsAvailable);

// 1b. GetListingRentalSnapshots (theo danh sách): POST /equipment-listings/batch, checkout đọc mọi Listing của giỏ trong một lần gọi.
// Inventory nhận 1–100 mã, bỏ mã trùng và từ chối ô lạ.
public sealed record ListingRentalSnapshotsRequest(
    IReadOnlyList<Guid> EquipmentListingIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool IncludeWarehouse = false);

public sealed record ListingRentalSnapshotsResponse(
    IReadOnlyList<ListingRentalSnapshotDto> Items,
    IReadOnlyList<Guid> MissingIds);

// 3. CreateHold: POST /equipment-listings/{listingId}/holds, đúng dạng CreateListingHoldRequest của Inventory
// (Inventory từ chối mọi ô lạ). EndDate là ngày dùng máy cuối (có tính); ListingId nằm trên đường dẫn.
// Gửi lại cùng SourceSystem + GroupReference thì Inventory trả đúng nhóm máy cũ với IsReplay = true.
// Các lệnh Kafka sau này của Inventory cũng tìm nhóm máy theo "rental" + "rental-item:{RentalItemId}".
public sealed record CreateHoldRequest(
    [property: JsonIgnore] Guid ListingId,
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset HoldExpiresAt,
    string SourceSystem,
    string GroupReference,
    string? Notes = null);

/// <summary>Một máy Inventory đã giữ (AllocationResponse bên Inventory, Rental chỉ đọc các ô cần).</summary>
public sealed record HeldAllocationDto(
    Guid Id,
    Guid AssetId,
    string Status);

public sealed record CreateHoldResponse(
    Guid ListingId,
    string SourceSystem,
    string GroupReference,
    int Quantity,
    IReadOnlyList<HeldAllocationDto> Allocations,
    bool IsReplay);

/// <summary>Máy đã giữ cho một dòng checkout, dạng Rental dùng để lưu RentalUnitAssignment.</summary>
public sealed record AssignedAssetDto(
    Guid AllocationId,
    Guid AssetId,
    string Status);

// 4. ExtendHold
public sealed record ExtendHoldRequest(
    DateTimeOffset NewHoldExpiresAt,
    string OperationKey);

public sealed record ExtendHoldResponse(
    string ReceiptRef,
    string AllocationGroupId,
    DateTimeOffset NewHoldExpiresAt,
    DateTimeOffset AppliedAt,
    bool IsReplay);

// 5. ConfirmAllocation
public sealed record ConfirmAllocationRequest(
    string OperationKey);

public sealed record ConfirmedAllocationUnitDto(
    Guid AllocationId,
    Guid AssetId,
    string Status);

public sealed record ConfirmAllocationResponse(
    string ReceiptRef,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay,
    IReadOnlyList<ConfirmedAllocationUnitDto> Units);

// 6. ReleaseAllocation
public sealed record ReleaseAllocationRequest(
    string OperationKey,
    string Reason);

public sealed record ReleaseAllocationResponse(
    string ReceiptRef,
    string OperationKey,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay);

// 7. CancelAllocation
public sealed record CancelAllocationRequest(
    string OperationKey,
    string Reason);

public sealed record CancelAllocationResponse(
    string ReceiptRef,
    string OperationKey,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay);

// 8. MarkAssetsOverdue
public sealed record MarkAssetsOverdueRequest(
    IReadOnlyList<Guid> AllocationIds,
    string OperationKey);

public sealed record OverdueUnitDto(
    Guid AllocationId,
    Guid AssetId,
    string Status,
    string OverdueProtectionRef);

public sealed record MarkAssetsOverdueResponse(
    string ReceiptRef,
    string OperationKey,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay,
    IReadOnlyList<OverdueUnitDto> Units);

// 9. MarkAssetsReturned
public sealed record ReturnedUnitItemRequest(
    Guid AllocationId,
    UnitCondition Condition);

public sealed record MarkAssetsReturnedRequest(
    Guid ReturnRecordId,
    DateTimeOffset ActualReturnAt,
    IReadOnlyList<ReturnedUnitItemRequest> Units,
    string OperationKey);

public sealed record ReturnedUnitItemResponse(
    Guid AllocationId,
    Guid AssetId,
    string Status,
    DateTimeOffset? AllocationClosedAt,
    string? DamageDispositionRef);

public sealed record MarkAssetsReturnedResponse(
    string ReceiptRef,
    string OperationKey,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay,
    IReadOnlyList<ReturnedUnitItemResponse> Units);

// 10. BlockDamagedAssets
public sealed record BlockDamagedAssetsRequest(
    IReadOnlyList<Guid> AllocationIds,
    Guid ReturnRecordId,
    string Reason,
    string OperationKey);

public sealed record BlockedDamagedUnitResponse(
    Guid AllocationId,
    Guid AssetId,
    string Status,
    string DamageDispositionRef);

public sealed record BlockDamagedAssetsResponse(
    string ReceiptRef,
    string OperationKey,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay,
    IReadOnlyList<BlockedDamagedUnitResponse> Units);
