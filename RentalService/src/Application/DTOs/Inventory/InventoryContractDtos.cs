using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Inventory;

// 1. GetListingRentalSnapshot
// Theo dạng Inventory đưa ngày 15/10/2026: chỉ những gì Rental cần để tính tiền, chia đơn con và hiển thị.
// Inventory chưa có tiền cọc, Rental tạm tính cọc = 0.
public sealed record ListingRentalSnapshotDto(
    Guid Id,
    Guid OwnerUserId,
    string Name,
    Guid? ThumbnailMediaId,
    decimal DailyRate,
    string Currency,
    string Status);

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

// 1b. GetListingRentalSnapshots (theo danh sách): checkout đọc mọi Listing của giỏ trong một lần gọi.
// Dạng request ({ "ids": [...] }) và đường dẫn chưa chốt với Inventory.
public sealed record ListingRentalSnapshotsRequest(
    IReadOnlyList<Guid> Ids);

public sealed record ListingRentalSnapshotsResponse(
    IReadOnlyList<ListingRentalSnapshotDto> Items,
    IReadOnlyList<Guid> MissingIds);

// 2b. CheckListingAvailabilityBatch (theo danh sách): mỗi dòng có Listing, khoảng ngày [From, To) và số lượng riêng.
public sealed record ListingAvailabilityBatchLine(
    Guid ListingId,
    DateOnly From,
    DateOnly To,
    int Quantity);

public sealed record ListingAvailabilityBatchRequest(
    IReadOnlyList<ListingAvailabilityBatchLine> Lines);

public sealed record ListingAvailabilityBatchResult(
    Guid ListingId,
    DateOnly From,
    DateOnly To,
    int AvailableQuantity,
    bool IsAvailable);

public sealed record ListingAvailabilityBatchResponse(
    IReadOnlyList<ListingAvailabilityBatchResult> Results);

// 3. CreateHold
// Inventory không kiểm tra phiên bản Listing khi giữ máy nên Rental không gửi ExpectedSnapshotVersion nữa.
// Các ô còn lại vẫn lệch với Inventory (startDate/endDate, groupReference…), sẽ sửa riêng.
public sealed record CreateHoldRequest(
    Guid ListingId,
    int Quantity,
    DateOnly From,
    DateOnly To,
    string ExternalReference,
    DateTimeOffset RequestedHoldExpiresAt);

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
