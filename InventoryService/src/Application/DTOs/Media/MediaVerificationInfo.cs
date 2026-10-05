namespace Dprental.InventoryService.Application.DTOs.Media;

/// <summary>
/// DTO chứa thông tin kết quả xác minh media từ MediaService.
/// </summary>
public sealed record MediaVerificationInfo(
    Guid MediaId,
    string Status,
    string UploadedByUserId
);
