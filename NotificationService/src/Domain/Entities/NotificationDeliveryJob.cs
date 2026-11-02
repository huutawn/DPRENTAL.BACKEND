namespace Dprental.NotificationService.Domain.Entities;

/// <summary>
/// Công việc gửi thông báo qua các kênh ngoại vi (Email) lưu trữ tại notification.delivery_jobs.
/// </summary>
public sealed class NotificationDeliveryJob
{
    public Guid Id { get; set; }
    public Guid NotificationId { get; set; }
    public Notification? Notification { get; set; }
    public string Channel { get; set; } = string.Empty; // EMAIL
    public Guid RecipientUserId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Succeeded, RetryScheduled, Uncertain, Failed, Suppressed
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }
    public DateTimeOffset? DispatchStartedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
