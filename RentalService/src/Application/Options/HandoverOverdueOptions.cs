namespace Dprental.RentalService.Application.Options;

/// <remarks>
/// Giá trị cấu hình cho worker và service quá hạn bàn giao; policy dùng số giờ gia hạn để tính deadline.
/// Options chỉ chứa tham số, không tự quét hoặc chuyển trạng thái item.
/// </remarks>
public sealed class HandoverOverdueOptions
{
    public const string SectionName = "Rental:HandoverOverdue";

    /// <summary>Bật/tắt worker quét quá hạn bàn giao; không tự thay đổi các item đã tồn tại.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Khoảng nghỉ giữa các vòng quét, tính bằng giây.</summary>
    public int ScanIntervalSeconds { get; set; } = 60;
    /// <summary>Số order tối đa lấy ra để xử lý trong một batch.</summary>
    public int BatchSize { get; set; } = 50;
    /// <summary>Số giờ gia hạn kể từ mốc cảnh báo D+1, 00:00 giờ Việt Nam; mặc định 24 giờ.</summary>
    public int AutoExpireGraceHours { get; set; } = 24;
}
