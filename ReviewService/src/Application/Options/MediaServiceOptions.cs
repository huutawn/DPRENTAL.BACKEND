namespace Dprental.ReviewService.Application.Options;

/// <summary>
/// Cấu hình kết nối tới MediaService.
/// </summary>
public sealed class MediaServiceOptions
{
    public const string SectionName = "MediaService";

    public string BaseUrl { get; set; } = "http://media-service:8080";
    public string ApiKey { get; set; } = "dev-internal-api-key-dprental-2026";
    public int TimeoutSeconds { get; set; } = 5;
}
