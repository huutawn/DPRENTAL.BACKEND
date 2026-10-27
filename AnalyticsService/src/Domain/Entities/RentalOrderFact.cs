namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Một dòng read model cho một đơn thuê. Chỉ chứa dữ liệu, không chứa quy tắc nghiệp vụ.
/// </summary>
public sealed class RentalOrderFact
{
    public Guid RentalOrderId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string Status { get; set; } = "UNKNOWN";

    // orderVersion của event cuối đã đổi Status; 0 = chưa nhận event nào có version
    public long StatusVersion { get; set; }
}
