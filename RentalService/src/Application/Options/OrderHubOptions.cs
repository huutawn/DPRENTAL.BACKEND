namespace Dprental.RentalService.Application.Options;

/// <summary>
/// Cấu hình nguồn kết nối được phép, timeout và giới hạn truyền nhận SignalR của OrderHub.
/// </summary>
/// <remarks>
/// AddOrderHub đọc các tham số này để cấu hình SignalR và kiểm soát Origin.
/// Đây là cấu hình kết nối realtime, không phải quy tắc chuyển trạng thái đơn hàng.
/// </remarks>
public sealed class OrderHubOptions
{
    public const string SectionName = "OrderHub";

    /// <summary>Danh sách Origin được phép kết nối Hub, dùng cho bước kiểm tra nguồn request.</summary>
    public string[] AllowedOrigins { get; set; } = [];
    /// <summary>Cho phép SignalR trả lỗi chi tiết; mặc định tắt.</summary>
    public bool EnableDetailedErrors { get; set; }
    /// <summary>Thời gian tối đa chờ hoàn tất bắt tay SignalR, tính bằng giây.</summary>
    public int HandshakeTimeoutSeconds { get; set; } = 15;
    /// <summary>Chu kỳ gửi tín hiệu giữ kết nối, tính bằng giây.</summary>
    public int KeepAliveIntervalSeconds { get; set; } = 15;
    /// <summary>Thời gian không nhận tín hiệu từ client trước khi coi kết nối đã mất, tính bằng giây.</summary>
    public int ClientTimeoutIntervalSeconds { get; set; } = 30;
    /// <summary>Kích thước tối đa của một message nhận từ client, tính bằng byte.</summary>
    public long MaximumReceiveMessageSize { get; set; } = 32 * 1024;
}
