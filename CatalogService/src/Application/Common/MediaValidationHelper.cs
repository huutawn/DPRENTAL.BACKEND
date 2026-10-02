namespace Dprental.CatalogService.Application.Common;

/// <summary>
/// Các quy tắc kiểm tra định dạng và trạng thái media được chấp nhận trong Catalog.
/// </summary>
public static class MediaValidationHelper
{
    public static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    public static bool IsAllowedImageType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && AllowedImageMimeTypes.Contains(contentType.Trim());

    public static bool IsReady(string? status) =>
        string.Equals(status?.Trim(), "Ready", StringComparison.OrdinalIgnoreCase);
}
