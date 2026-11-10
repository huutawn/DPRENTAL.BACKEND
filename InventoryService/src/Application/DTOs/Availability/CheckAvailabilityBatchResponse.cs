
namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Kết quả kiểm tra tính khả dụng hàng loạt tài sản theo DPR-150.
/// Chỉ chứa unavailableAssetIds và notFoundAssetIds theo hợp đồng MVP.
/// </summary>
public sealed record CheckAvailabilityBatchResponse(
    IReadOnlyList<Guid> UnavailableAssetIds,
    IReadOnlyList<Guid> NotFoundAssetIds
);
