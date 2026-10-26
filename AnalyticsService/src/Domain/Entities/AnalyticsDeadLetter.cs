namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Vị trí của một message Kafka xử lý lỗi. Không lưu nội dung message để tránh lộ dữ liệu nhạy cảm.
/// </summary>
public sealed class AnalyticsDeadLetter
{
    public Guid Id { get; set; } // Khóa chính do Analytics tự tạo, vì message hỏng có thể không có messageId

    // Vị trí message lỗi; bộ ba này là duy nhất để mỗi message lỗi chỉ ghi một lần.
    // Muốn xử lý lại thì đọc lại message từ Kafka theo vị trí này.
    public string Topic { get; set; } = string.Empty;
    public int Partition { get; set; }
    public long Offset { get; set; }

    // Message hỏng có thể không đọc được envelope → nullable
    public Guid? MessageId { get; set; }
    public string? MessageType { get; set; }

    public string ErrorCode { get; set; } = string.Empty; // Lý do lỗi, ví dụ tiền có phần lẻ

    public DateTimeOffset FailedAt { get; set; } // Lúc xử lý lỗi
}
