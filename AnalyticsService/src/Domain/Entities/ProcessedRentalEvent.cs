namespace Dprental.AnalyticsService.Domain.Entities;

public sealed class ProcessedRentalEvent
{
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
