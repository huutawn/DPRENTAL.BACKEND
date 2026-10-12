using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Inventory;

// 1. GetListingRentalSnapshot
public sealed record ListingRentalSnapshotDto(
    Guid ListingId,
    Guid SupplierId,
    string DisplayName,
    string? Description,
    string PickupLocation,
    decimal DailyRate,
    decimal DepositPerUnit,
    string Currency,
    string BookingStatus,
    string SnapshotVersion,
    DateTimeOffset CapturedAt);

// 2. CheckListingAvailability
public sealed record ListingAvailabilityCheckRequest(
    Guid ListingId,
    DateOnly From,
    DateOnly To,
    int Quantity);

public sealed record ListingAvailabilityCheckResponse(
    int AvailableQuantity,
    bool IsAvailable,
    DateTimeOffset EvaluatedAt,
    DateOnly From,
    DateOnly To);

// 3. CreateHold
public sealed record CreateHoldRequest(
    Guid ListingId,
    int Quantity,
    DateOnly From,
    DateOnly To,
    string ExternalReference,
    DateTimeOffset RequestedHoldExpiresAt,
    string ExpectedSnapshotVersion);

public sealed record AssignedAssetDto(
    Guid AllocationId,
    Guid AssetId,
    string Status);

public sealed record CreateHoldResponse(
    string AllocationGroupId,
    IReadOnlyList<AssignedAssetDto> AssignedAssets,
    DateTimeOffset HoldExpiresAt,
    Guid ListingId,
    int Quantity,
    DateOnly From,
    DateOnly To,
    bool IsReplay,
    string AllocationStatus);

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
