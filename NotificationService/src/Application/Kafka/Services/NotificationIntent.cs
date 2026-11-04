namespace Dprental.NotificationService.Application.Kafka.Services;

/// <summary>
/// Model đại diện cho ý định xử lý thông báo trích xuất từ sự kiện Kafka.
/// </summary>
public sealed record NotificationIntent
{
    public string EventType { get; init; } = string.Empty;
    public Guid? OrderId { get; init; }
    public Guid? RentalItemId { get; init; }
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string? WatermarkScope { get; init; }
    public Guid? WatermarkId { get; init; }
    public long OrderVersion { get; init; }
    public bool IsTerminal { get; init; }
    public string? TerminalState { get; init; }
    public Guid? RenterId { get; init; }
    public Guid? SupplierId { get; init; }
    public Guid? ReturnRequestId { get; init; }
    public Dictionary<string, object?> RenderData { get; init; } = new();
    public bool IsPaid { get; init; }
    public Guid? FinancialId { get; init; }
}
