namespace Dprental.InventoryService.Application.DTOs.Media;

public sealed record MediaVerificationInfo(
    Guid MediaId,
    string Status,
    string UploadedByUserId,
    string ContentType);
