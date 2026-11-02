namespace Dprental.NotificationService.Domain.Entities;

/// <summary>
/// Tùy chọn bật/tắt nhận email theo category của từng người dùng lưu tại notification.preferences.
/// </summary>
public sealed class NotificationPreference
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Category { get; set; } = string.Empty; // ORDER, PAYMENT, HANDOVER, RETURN
    public bool EmailEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
