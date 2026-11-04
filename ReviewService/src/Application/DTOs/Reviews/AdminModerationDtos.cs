namespace Dprental.ReviewService.Application.DTOs.Reviews;

public sealed record AdminModerationRequest(string? Reason);

public sealed record AdminModerationResponse(
    Guid ReviewId,
    string AuthorState,
    string ModerationState,
    long Version,
    DateTimeOffset ModeratedAt,
    Guid ModeratorId,
    string? Reason);

public sealed record CreateReviewReportRequest(string Reason);

public sealed record ReviewReportResponse(
    Guid Id,
    Guid ReviewId,
    Guid ReporterId,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy);

public sealed record ResolveReviewReportRequest(string Resolution);
