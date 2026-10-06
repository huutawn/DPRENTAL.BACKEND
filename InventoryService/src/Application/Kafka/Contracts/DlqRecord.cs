namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Transport record chuẩn hóa của Dead Letter Queue (DLQ).
/// Lưu trữ raw message, thông tin vị trí nguồn (topic, partition, offset), handler tiếp nhận,
/// số lần retry và mã lỗi. Không giả tạo OrganizationId khi envelope không đọc được.
/// </summary>
public sealed record DlqRecord
{
    public required string RawMessage { get; init; }
    public required string SourceTopic { get; init; }
    public required int SourcePartition { get; init; }
    public required long SourceOffset { get; init; }
    public required string Handler { get; init; }
    public required int Attempts { get; init; }
    public required string ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset FailedAt { get; init; }
}
