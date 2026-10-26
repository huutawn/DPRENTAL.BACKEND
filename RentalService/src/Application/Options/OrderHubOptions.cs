namespace Dprental.RentalService.Application.Options;

/// <summary>
/// Cấu hình cho OrderHub (đường dẫn, Origin allowlist và SignalR transport limits).
/// </summary>
public sealed class OrderHubOptions
{
    public const string SectionName = "OrderHub";

    public string[] AllowedOrigins { get; set; } = [];
    public bool EnableDetailedErrors { get; set; }
    public int HandshakeTimeoutSeconds { get; set; } = 15;
    public int KeepAliveIntervalSeconds { get; set; } = 15;
    public int ClientTimeoutIntervalSeconds { get; set; } = 30;
    public long MaximumReceiveMessageSize { get; set; } = 32 * 1024;
}
