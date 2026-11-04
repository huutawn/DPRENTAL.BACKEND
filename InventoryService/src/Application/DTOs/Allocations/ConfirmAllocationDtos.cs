using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu xác nhận nhóm phân bổ (Hold -> Allocated).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmAllocationRequest(
    string OperationKey
);

/// <summary>
/// Chi tiết đơn vị đã được xác nhận phân bổ.
/// </summary>
public sealed record ConfirmedAllocationUnitDto(
    Guid AllocationId,
    Guid AssetId,
    string Status
);

/// <summary>
/// Phản hồi kết quả xác nhận nhóm phân bổ tương thích hợp đồng RentalService.
/// </summary>
public sealed record ConfirmAllocationResponse(
    string ReceiptRef,
    string AllocationGroupId,
    DateTimeOffset AppliedAt,
    bool IsReplay,
    IReadOnlyList<ConfirmedAllocationUnitDto> Units
);
