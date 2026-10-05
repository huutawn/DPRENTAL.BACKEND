namespace Dprental.InventoryService.Application.DTOs.Media;

/// <summary>
/// DTO chứa thông tin kết quả cập nhật visibility từ MediaService.
/// </summary>
public sealed record MediaVisibilityInfo(
    Guid MediaId,
    string Visibility
);
