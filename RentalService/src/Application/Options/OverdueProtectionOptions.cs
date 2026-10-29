namespace Dprental.RentalService.Application.Options;

/// <remarks>
/// Điều khiển bật/tắt, chu kỳ quét và kích thước batch cho luồng bảo vệ thiết bị thuê quá hạn.
/// OverdueProtectionService và executor điều phối nghiệp vụ; options không tự giải phóng allocation.
/// </remarks>
public sealed class OverdueProtectionOptions
{
    public const string SectionName = "Rental:OverdueProtection";

    /// <summary>Bật/tắt luồng bảo vệ thiết bị đã quá hạn thuê nhưng chưa trả.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Khoảng nghỉ giữa các vòng quét bảo vệ quá hạn, tính bằng giây.</summary>
    public int ScanIntervalSeconds { get; set; } = 60;
    /// <summary>Số order tối đa được chọn trong một batch quét.</summary>
    public int BatchSize { get; set; } = 50;
}
