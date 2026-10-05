using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu chuyển trạng thái giữ chỗ sang phân bổ chính thức (Held -> Allocated).
/// Từ chối các thuộc tính không xác định (Disallow unmapped members).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AllocateRequest(
    [property: JsonPropertyName("operationKey")] string OperationKey
);
