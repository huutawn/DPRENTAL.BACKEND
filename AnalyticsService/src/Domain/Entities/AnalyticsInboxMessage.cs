namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Đánh dấu một message Kafka đã xử lý, để bỏ qua khi message bị gửi lại.
/// </summary>
public sealed class AnalyticsInboxMessage
{
    public Guid MessageId { get; set; } // Khóa chính; messageId của envelope. Gặp lại → bỏ qua, chống cộng trùng

    public string MessageType { get; set; } = string.Empty; // Tên event, ví dụ RentalOrderPaid; để điều tra

    // Vị trí message trong Kafka, dùng khi cần tìm lại message gốc
    public string Topic { get; set; } = string.Empty; // Tên topic, ví dụ rental.events.v1
    public int Partition { get; set; }                // Số phần của topic (0, 1, 2...)
    public long Offset { get; set; }                  // Số thứ tự message trong partition, tăng mãi nên dùng long

    public DateTimeOffset ProcessedAt { get; set; } // Lúc Analytics xử lý xong
}
