using System.Globalization;
using System.Text.Json;
using Dprental.ReviewService.Application.Kafka.Events;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Dịch vụ xử lý kiểm duyệt đánh giá, báo cáo vi phạm và quyết định của Admin với transaction qua IUnitOfWork.
/// </summary>
public sealed class ReviewModerationService : IReviewModerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IReviewRepository _reviewRepository;
    private readonly IReviewReportRepository _reportRepository;
    private readonly IReviewAuditRepository _auditRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewModerationService>? _logger;

    public ReviewModerationService(
        IUnitOfWork unitOfWork,
        IReviewRepository reviewRepository,
        IReviewReportRepository reportRepository,
        IReviewAuditRepository auditRepository,
        IOutboxRepository outboxRepository,
        TimeProvider? timeProvider = null,
        ILogger<ReviewModerationService>? logger = null)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _reviewRepository = reviewRepository ?? throw new ArgumentNullException(nameof(reviewRepository));
        _reportRepository = reportRepository ?? throw new ArgumentNullException(nameof(reportRepository));
        _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ReviewReportResponse>> CreateReportAsync(
        Guid reviewId,
        Guid reporterId,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (reviewId == Guid.Empty || reporterId == Guid.Empty || string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure<ReviewReportResponse>(
                Error.Validation("VALIDATION_FAILED", "ReviewId, ReporterId và Reason là bắt buộc."));
        }

        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá cần báo cáo."));
        }

        // Kiểm tra quyền nhìn thấy review: người thứ ba không được biết sự tồn tại của review riêng tư hoặc đã bị ẩn/rút
        bool isPublic = review.Direction == ReviewDirection.renter_to_supplier_listing &&
                        review.AuthorState == AuthorState.Active &&
                        review.ModerationState == ModerationState.Visible;
        bool isParticipant = reporterId == review.AuthorId || reporterId == review.SubjectId;

        if (!isPublic && !isParticipant)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá cần báo cáo."));
        }

        if (reason.Length > 1000)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.Validation("INVALID_REASON_LENGTH", "Lý do báo cáo không được vượt quá 1.000 ký tự."));
        }
        var existingReport = await _reportRepository.GetByReviewAndReporterAsync(reviewId, reporterId, cancellationToken);
        if (existingReport != null)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.Conflict("REPORT_ALREADY_EXISTS", "Bạn đã gửi báo cáo vi phạm cho đánh giá này rồi."));
        }

        var now = _timeProvider.GetUtcNow();
        var report = new ReviewReport(Guid.NewGuid(), reviewId, reporterId, reason, now);

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _reportRepository.AddAsync(report, cancellationToken);
            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger?.LogError(ex, "Lỗi khi lưu báo cáo vi phạm cho review {ReviewId}", reviewId);
            throw;
        }

        return Result.Success(new ReviewReportResponse(
            report.Id,
            report.ReviewId,
            report.ReporterId,
            report.Reason,
            report.Status.ToString(),
            report.CreatedAt,
            report.ResolvedAt,
            report.ResolvedBy,
            report.Version));
    }

    /// <inheritdoc />
    public async Task<Result<AdminModerationResponse>> HideReviewAsync(
        Guid reviewId,
        Guid adminId,
        string? reason,
        string ifMatch,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<AdminModerationResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá."));
        }

        if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
        {
            return Result.Failure<AdminModerationResponse>(ifMatchError!);
        }

        var now = _timeProvider.GetUtcNow();
        review.AdminHide(reason ?? "Nội dung vi phạm tiêu chuẩn cộng đồng.", now);

        var audit = new ReviewAudit(
            Guid.NewGuid(),
            review.Id,
            adminId,
            "Admin",
            ReviewAuditAction.Hidden,
            reason,
            null,
            null,
            now);

        var outbox = new OutboxMessage(
            Guid.NewGuid(),
            ReviewEventFactory.PublicEventsTopic,
            "ReviewHidden",
            review.ListingId.ToString("D"),
            JsonSerializer.Serialize(ReviewEventFactory.CreateContributionEvent(review), JsonOptions),
            now);

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _reviewRepository.UpdateAsync(review, cancellationToken);
            await _auditRepository.AddAsync(audit, cancellationToken);
            await _outboxRepository.AddAsync(outbox, cancellationToken);
            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger?.LogError(ex, "Lỗi khi ẩn đánh giá {ReviewId}", reviewId);
            throw;
        }

        return Result.Success(new AdminModerationResponse(
            review.Id,
            review.AuthorState.ToString(),
            review.ModerationState.ToString(),
            review.Version,
            now,
            adminId,
            review.AdminReason));
    }

    /// <inheritdoc />
    public async Task<Result<AdminModerationResponse>> RestoreReviewAsync(
        Guid reviewId,
        Guid adminId,
        string? reason,
        string ifMatch,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<AdminModerationResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá."));
        }

        if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
        {
            return Result.Failure<AdminModerationResponse>(ifMatchError!);
        }

        var now = _timeProvider.GetUtcNow();
        review.AdminRestore();

        var audit = new ReviewAudit(
            Guid.NewGuid(),
            review.Id,
            adminId,
            "Admin",
            ReviewAuditAction.Restored,
            reason,
            null,
            null,
            now);

        var outbox = new OutboxMessage(
            Guid.NewGuid(),
            ReviewEventFactory.PublicEventsTopic,
            "ReviewRestored",
            review.ListingId.ToString("D"),
            JsonSerializer.Serialize(ReviewEventFactory.CreateContributionEvent(review), JsonOptions),
            now);

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _reviewRepository.UpdateAsync(review, cancellationToken);
            await _auditRepository.AddAsync(audit, cancellationToken);
            await _outboxRepository.AddAsync(outbox, cancellationToken);
            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger?.LogError(ex, "Lỗi khi phục hồi đánh giá {ReviewId}", reviewId);
            throw;
        }

        return Result.Success(new AdminModerationResponse(
            review.Id,
            review.AuthorState.ToString(),
            review.ModerationState.ToString(),
            review.Version,
            now,
            adminId,
            reason));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<ReviewReportResponse>>> GetReportsAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 50) pageSize = 50;

        var (items, totalCount) = await _reportRepository.GetReportsAsync(page, pageSize, cancellationToken);

        var responses = items.Select(r => new ReviewReportResponse(
            r.Id,
            r.ReviewId,
            r.ReporterId,
            r.Reason,
            r.Status.ToString(),
            r.CreatedAt,
            r.ResolvedAt,
            r.ResolvedBy,
            r.Version)).ToList();

        return Result.Success(PagedResult<ReviewReportResponse>.Create(
            items: responses,
            page: page,
            pageSize: pageSize,
            totalCount: totalCount));
    }

    /// <inheritdoc />
    public async Task<Result<ReviewReportResponse>> ResolveReportAsync(
        Guid reportId,
        Guid adminId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        var report = await _reportRepository.GetByIdAsync(reportId, cancellationToken);
        if (report == null)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.NotFound("REPORT_NOT_FOUND", "Không tìm thấy báo cáo vi phạm."));
        }

        if (report.Status != ReportStatus.PendingReview)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.Conflict("REPORT_ALREADY_RESOLVED", "Báo cáo vi phạm này đã được xử lý trước đó."));
        }

        if (!Enum.TryParse<ReportStatus>(resolution, true, out var status) || status == ReportStatus.PendingReview)
        {
            return Result.Failure<ReviewReportResponse>(
                Error.Validation("INVALID_RESOLUTION", "Độ phân giải phải là 'ReviewedDismissed' hoặc 'ReviewedHidden'."));
        }

        var now = _timeProvider.GetUtcNow();
        report.Resolve(status, adminId, now);

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _reportRepository.UpdateAsync(report, cancellationToken);

            if (status == ReportStatus.ReviewedHidden)
            {
                var review = await _reviewRepository.GetByIdAsync(report.ReviewId, cancellationToken);
                if (review != null && review.ModerationState == ModerationState.Visible)
                {
                    review.AdminHide(report.Reason, now);
                    await _reviewRepository.UpdateAsync(review, cancellationToken);

                    var audit = new ReviewAudit(
                        Guid.NewGuid(),
                        review.Id,
                        adminId,
                        "Admin",
                        ReviewAuditAction.Hidden,
                        report.Reason,
                        null,
                        null,
                        now);

                    var outbox = new OutboxMessage(
                        Guid.NewGuid(),
                        ReviewEventFactory.PublicEventsTopic,
                        "ReviewHidden",
                        review.ListingId.ToString("D"),
                        JsonSerializer.Serialize(ReviewEventFactory.CreateContributionEvent(review), JsonOptions),
                        now);

                    await _auditRepository.AddAsync(audit, cancellationToken);
                    await _outboxRepository.AddAsync(outbox, cancellationToken);
                }
            }

            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger?.LogError(ex, "Lỗi khi xử lý báo cáo {ReportId}", reportId);
            throw;
        }

        return Result.Success(new ReviewReportResponse(
            report.Id,
            report.ReviewId,
            report.ReporterId,
            report.Reason,
            report.Status.ToString(),
            report.CreatedAt,
            report.ResolvedAt,
            report.ResolvedBy,
            report.Version));
    }

    private static bool ParseIfMatch(string? ifMatch, long currentVersion, out Error? error)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            error = Error.PreconditionRequired("PRECONDITION_REQUIRED", "Header If-Match chứa ETag phiên bản là bắt buộc đối với thao tác này.");
            return false;
        }

        string cleaned = ifMatch.Trim();

        if (cleaned == "*")
        {
            error = Error.Validation("INVALID_IF_MATCH", "Header If-Match không được sử dụng ký tự đại diện (*).");
            return false;
        }

        if (cleaned.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            error = Error.Validation("WEAK_ETAG_NOT_SUPPORTED", "Hệ thống chỉ chấp nhận Strong ETag dạng số nguyên phiên bản, không chấp nhận Weak ETag (W/).");
            return false;
        }

        cleaned = cleaned.Trim('"');

        if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion) || parsedVersion <= 0)
        {
            error = Error.Validation("INVALID_IF_MATCH", "Giá trị If-Match không hợp lệ. Phải là số nguyên phiên bản dương trong dấu ngoặc kép.");
            return false;
        }

        if (parsedVersion != currentVersion)
        {
            error = Error.PreconditionFailed(
                "ETAG_PRECONDITION_FAILED",
                $"Phiên bản đánh giá không khớp. Phiên bản cung cấp là {parsedVersion}, phiên bản hiện tại là {currentVersion}.");
            return false;
        }

        error = null;
        return true;
    }
}
