namespace Dprental.NotificationService.Domain.Entities;

/// <summary>
/// Đại diện cho bản ghi thông báo trong ứng dụng lưu trữ tại notification.notifications.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid? OrderId { get; set; }
    public string NotificationType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public Guid SourceEventId { get; set; }
    public string SourceProducer { get; set; } = string.Empty;
    public string SourceEventType { get; set; } = string.Empty;
    public long EventVersion { get; set; }
    public string TemplateCode { get; set; } = string.Empty;
    public string TemplateVersion { get; set; } = string.Empty;
    public string? RenderDataJson { get; set; }
    public string? DeepLinkUrl { get; set; }
    public bool IsActionable { get; set; }
    public string? ActionUrl { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    public ICollection<NotificationDeliveryJob> DeliveryJobs { get; set; } = new List<NotificationDeliveryJob>();
}
