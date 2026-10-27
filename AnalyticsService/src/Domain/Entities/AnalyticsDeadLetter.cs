namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Vị trí của một message Kafka xử lý lỗi dữ liệu. Không lưu nội dung message để tránh lộ dữ liệu.
/// Muốn xử lý lại thì đọc lại message từ Kafka theo (Topic, Partition, Offset).
/// </summary>
public sealed class AnalyticsDeadLetter
{
    public Guid Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public int Partition { get; set; }
    public long Offset { get; set; }
    public Guid? MessageId { get; set; }      // Null khi message hỏng tới mức không đọc được envelope
    public string? MessageType { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public DateTimeOffset FailedAt { get; set; }
}
