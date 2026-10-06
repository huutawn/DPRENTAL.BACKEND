namespace Dprental.InventoryService.Domain.Clients.Media;

public sealed record MediaVerificationInfo(
    Guid MediaId,
    string Status,
    string UploadedByUserId);
