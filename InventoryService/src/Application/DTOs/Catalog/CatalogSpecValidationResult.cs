namespace Dprental.InventoryService.Application.DTOs.Catalog;

/// <summary>
/// Kết quả thẩm tra specification của tài sản.
/// </summary>
public sealed record CatalogSpecValidationResult(CatalogSpecValidationStatus Status, string? Message = null)
{
    public static CatalogSpecValidationResult Valid() =>
        new(CatalogSpecValidationStatus.Valid);

    public static CatalogSpecValidationResult Invalid(string message) =>
        new(CatalogSpecValidationStatus.Invalid, message);

    public static CatalogSpecValidationResult NotSupported(string? message = null) =>
        new(CatalogSpecValidationStatus.NotSupported, message ?? "CatalogService chưa hỗ trợ canonical spec validation trong MVP.");
}
