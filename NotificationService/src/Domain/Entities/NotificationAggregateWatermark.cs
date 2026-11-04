namespace Dprental.NotificationService.Domain.Entities;

public static class NotificationWatermarkScope
{
    public const string RentalOrder = "RentalOrder";
    public const string RentalItem = "RentalItem";
    public const string ReturnRequest = "ReturnRequest";
}

/// <summary>
/// Quản lý phiên bản nghiệp vụ tối đa theo aggregate và recipient để ngăn chặn thông báo lỗi thời lưu tại notification.aggregate_watermarks.
/// </summary>
public sealed class NotificationAggregateWatermark
{
    public string Scope { get; set; } = NotificationWatermarkScope.RentalOrder;
    public Guid AggregateId { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid? ParentOrderId { get; set; }
    public long LastOrderVersion { get; set; }
    public string? LastMessageId { get; set; }
    public bool IsTerminal { get; set; }
    public string? TerminalState { get; set; }
    public bool IsPaid { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
