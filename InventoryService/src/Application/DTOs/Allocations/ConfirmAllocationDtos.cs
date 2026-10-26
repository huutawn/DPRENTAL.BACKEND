using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu xác nhận nhóm phân bổ (Hold -> Allocated).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmAllocationRequest(
    [property: JsonPropertyName("operationKey")] string OperationKey
);

/// <summary>
/// Chi tiết đơn vị đã được xác nhận phân bổ.
/// </summary>
public sealed record ConfirmedAllocationUnitDto(
    [property: JsonPropertyName("allocationId")] Guid AllocationId,
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("status")] string Status
);

/// <summary>
/// Phản hồi kết quả xác nhận nhóm phân bổ tương thích hợp đồng RentalService.
/// </summary>
public sealed record ConfirmAllocationResponse(
    [property: JsonPropertyName("receiptRef")] string ReceiptRef,
    [property: JsonPropertyName("allocationGroupId")] string AllocationGroupId,
    [property: JsonPropertyName("appliedAt")] DateTimeOffset AppliedAt,
    [property: JsonPropertyName("isReplay")] bool IsReplay,
    [property: JsonPropertyName("units")] IReadOnlyList<ConfirmedAllocationUnitDto> Units
);
