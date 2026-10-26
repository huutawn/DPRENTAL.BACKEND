using System.ComponentModel.DataAnnotations;

namespace Dprental.AnalyticsService.Application.Hub.Options;

/// <summary>
/// Cấu hình SignalR Hub của Analytics, cùng bộ thông số với PaymentHubOptions.
/// </summary>
public sealed class AnalyticsHubOptions
{
    public const string SectionName = "AnalyticsHub";
    public const string DefaultHubPath = "/hubs/analytics";

    [Required]
    public string HubPath { get; set; } = DefaultHubPath;

    [Range(1, 120)]
    public int HandshakeTimeoutSeconds { get; set; } = 15;

    [Range(1, 120)]
    public int KeepAliveIntervalSeconds { get; set; } = 15;

    [Range(1, 120)]
    public int ClientTimeoutIntervalSeconds { get; set; } = 30;

    // Gửi chậm quá thì bỏ: số liệu đã lưu, FE tải lại sau vẫn thấy
    [Range(1, 10)]
    public int SendTimeoutSeconds { get; set; } = 2;

    // Client chỉ nhận tin, không gửi gì lên; giới hạn nhỏ để chặn lạm dụng
    [Range(1024, 1024 * 1024)]
    public long MaximumReceiveMessageSize { get; set; } = 32 * 1024;

    public bool EnableDetailedErrors { get; set; }

    /// <summary>Domain FE được phép kết nối. Rỗng = không kiểm tra Origin.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
