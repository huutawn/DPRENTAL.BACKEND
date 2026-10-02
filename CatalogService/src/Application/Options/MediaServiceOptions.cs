namespace Dprental.CatalogService.Application.Options;

/// <summary>
/// Cấu hình kết nối và tương tác với MediaService.
/// </summary>
public sealed class MediaServiceOptions
{
    public const string SectionName = "MediaService";

    public string InternalBaseUrl { get; set; } = "http://localhost:5004";

    public string PublicGatewayUrl { get; set; } = "http://localhost:5050";

    public string ApiKey { get; set; } = "dev-catalog-media-internal-key";

    public int TimeoutSeconds { get; set; } = 10;
}
