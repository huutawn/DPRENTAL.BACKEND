namespace Dprental.AnalyticsService.Domain.Entities;

public sealed class RentalOrderFact
{
    public Guid RentalOrderId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
    public string Status { get; set; } = "UNKNOWN";
    public long StatusVersion { get; set; }
}
