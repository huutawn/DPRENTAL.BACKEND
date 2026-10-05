using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Kết quả kiểm tra tính khả dụng hàng loạt tài sản theo DPR-150.
/// Chỉ chứa unavailableAssetIds và notFoundAssetIds theo hợp đồng MVP.
/// </summary>
public sealed record CheckAvailabilityBatchResponse(
    [property: JsonPropertyName("unavailableAssetIds")] IReadOnlyList<Guid> UnavailableAssetIds,
    [property: JsonPropertyName("notFoundAssetIds")] IReadOnlyList<Guid> NotFoundAssetIds
);
