using Dprental.ReviewService.Common.Enums;

namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Thực thể báo cáo vi phạm đánh giá do người dùng gửi tới Quản trị viên.
/// </summary>
public sealed class ReviewReport
{
    public Guid Id { get; private set; }
    public Guid ReviewId { get; private set; }
    public Guid ReporterId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public ReportStatus Status { get; private set; } = ReportStatus.PendingReview;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }

    private ReviewReport() { }

    public ReviewReport(
        Guid id,
        Guid reviewId,
        Guid reporterId,
        string reason,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id must not be empty.", nameof(id));
        if (reviewId == Guid.Empty) throw new ArgumentException("ReviewId must not be empty.", nameof(reviewId));
        if (reporterId == Guid.Empty) throw new ArgumentException("ReporterId must not be empty.", nameof(reporterId));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason must not be empty.", nameof(reason));

        Id = id;
        ReviewId = reviewId;
        ReporterId = reporterId;
        Reason = reason.Trim();
        Status = ReportStatus.PendingReview;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public void Resolve(ReportStatus resolution, Guid resolvedByAdmin, DateTimeOffset resolvedAt)
    {
        if (resolution == ReportStatus.PendingReview)
        {
            throw new InvalidOperationException("Cannot resolve a report back to PendingReview status.");
        }
        if (resolvedByAdmin == Guid.Empty)
        {
            throw new ArgumentException("ResolvedBy admin ID must not be empty.", nameof(resolvedByAdmin));
        }

        Status = resolution;
        ResolvedBy = resolvedByAdmin;
        ResolvedAt = resolvedAt.ToUniversalTime();
    }
}
