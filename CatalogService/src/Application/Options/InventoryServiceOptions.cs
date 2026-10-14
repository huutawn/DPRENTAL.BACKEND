namespace Dprental.CatalogService.Application.Options;

/// <summary>
/// Cấu hình kết nối và tương tác với InventoryService.
/// </summary>
public sealed class InventoryServiceOptions
{
    public const string SectionName = "InventoryService";

    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 3;
}
