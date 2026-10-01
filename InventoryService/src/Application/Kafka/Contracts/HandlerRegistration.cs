namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Đăng ký handler theo topic, message type/version, handler name và consumer group ID.
/// Các replica của cùng handler sử dụng cùng groupId; các handler độc lập dùng group riêng.
/// </summary>
public sealed record HandlerRegistration
{
    public required string Topic { get; init; }
    public required string MessageType { get; init; }
    public required int Version { get; init; }
    public required string HandlerName { get; init; }
    public required string GroupId { get; init; }
    public required Type HandlerType { get; init; }
    public required Type PayloadType { get; init; }

    /// <summary>
    /// Khóa phân loại duy nhất của handler đăng ký theo group ID, topic, type và version.
    /// </summary>
    public string RouteKey => $"{GroupId}::{Topic}::{MessageType}::v{Version}";
}
