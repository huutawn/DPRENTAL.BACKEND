namespace Dprental.AuthService.Application.Options;

/// <summary>Cấu hình kết nối tới MediaService và địa chỉ Gateway public.</summary>
public sealed class MediaServiceOptions
{
    public const string SectionName = "MediaService";

    /// <summary>Địa chỉ HTTP nội bộ của MediaService (ví dụ: http://media-service:8080).</summary>
    public string InternalBaseUrl { get; set; } = string.Empty;

    /// <summary>Địa chỉ công khai của Gateway (ví dụ: http://localhost:5050).</summary>
    public string PublicGatewayUrl { get; set; } = string.Empty;

    /// <summary>Khóa API nội bộ gửi trong header X-Api-Key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Thời gian chờ request (giây), mặc định 10 giây.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
