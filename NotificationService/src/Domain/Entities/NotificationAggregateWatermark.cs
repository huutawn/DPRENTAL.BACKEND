namespace Dprental.NotificationService.Domain.Entities;

/// <summary>
/// Quản lý phiên bản nghiệp vụ tối đa theo aggregate và recipient để ngăn chặn thông báo lỗi thời lưu tại notification.aggregate_watermarks.
/// </summary>
public sealed class NotificationAggregateWatermark
{
    public Guid AggregateId { get; set; }
    public Guid RecipientUserId { get; set; }
    public long LastOrderVersion { get; set; }
    public string? LastMessageId { get; set; }
    public bool IsTerminal { get; set; }
    public string? TerminalState { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
