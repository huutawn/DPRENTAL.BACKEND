using Dprental.ReviewService.Api.Filters;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Application.Security;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.ReviewService.Api.Controllers;

/// <summary>
/// Controller quản trị kiểm duyệt và xử lý vi phạm đánh giá (Admin API).
/// </summary>
[ApiController]
[Route("api/v1/admin/reviews")]
[Route("admin/reviews")]
public sealed class AdminReviewsController : ControllerBase
{
    private readonly IReviewService _reviewService;
    private readonly IReviewCallerContext _callerContext;

    public AdminReviewsController(
        IReviewService reviewService,
        IReviewCallerContext callerContext)
    {
        _reviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Quản trị viên ẩn một đánh giá vi phạm (POST /api/v1/admin/reviews/{reviewId}/hide).
    /// </summary>
    [HttpPost("{reviewId:guid}/hide")]
    public async Task<IActionResult> HideReview(
        [FromRoute] Guid reviewId,
        [FromBody] AdminModerationRequest? request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated || !_callerContext.ActorId.HasValue)
        {
            return Unauthorized(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Authentication is required."
            });
        }

        if (!_callerContext.IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Title = "Forbidden",
                Status = StatusCodes.Status403Forbidden,
                Detail = "Only administrators are permitted to perform moderation actions."
            });
        }

        var result = await _reviewService.HideReviewAsync(
            reviewId,
            _callerContext.ActorId.Value,
            request?.Reason,
            ifMatch ?? string.Empty,
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.ETag = $"\"{result.Value.Version}\"";
        return Ok(result.Value);
    }

    /// <summary>
    /// Quản trị viên khôi phục đánh giá về Visible (POST /api/v1/admin/reviews/{reviewId}/restore).
    /// </summary>
    [HttpPost("{reviewId:guid}/restore")]
    public async Task<IActionResult> RestoreReview(
        [FromRoute] Guid reviewId,
        [FromBody] AdminModerationRequest? request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated || !_callerContext.ActorId.HasValue)
        {
            return Unauthorized(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Authentication is required."
            });
        }

        if (!_callerContext.IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Title = "Forbidden",
                Status = StatusCodes.Status403Forbidden,
                Detail = "Only administrators are permitted to perform moderation actions."
            });
        }

        var result = await _reviewService.RestoreReviewAsync(
            reviewId,
            _callerContext.ActorId.Value,
            request?.Reason,
            ifMatch ?? string.Empty,
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.ETag = $"\"{result.Value.Version}\"";
        return Ok(result.Value);
    }

    /// <summary>
    /// Xem danh sách báo cáo vi phạm đánh giá (GET /api/v1/admin/reviews/reports).
    /// </summary>
    [HttpGet("reports")]
    public async Task<IActionResult> GetReports(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_callerContext.IsAuthenticated || !_callerContext.ActorId.HasValue)
        {
            return Unauthorized(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Authentication is required."
            });
        }

        if (!_callerContext.IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Title = "Forbidden",
                Status = StatusCodes.Status403Forbidden,
                Detail = "Only administrators are permitted to view review reports."
            });
        }

        var result = await _reviewService.GetReportsAsync(page, pageSize, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Xử lý giải quyết báo cáo vi phạm (POST /api/v1/admin/reviews/reports/{reportId}/resolve).
    /// </summary>
    [HttpPost("reports/{reportId:guid}/resolve")]
    public async Task<IActionResult> ResolveReport(
        [FromRoute] Guid reportId,
        [FromBody] ResolveReviewReportRequest request,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated || !_callerContext.ActorId.HasValue)
        {
            return Unauthorized(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Authentication is required."
            });
        }

        if (!_callerContext.IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Title = "Forbidden",
                Status = StatusCodes.Status403Forbidden,
                Detail = "Only administrators are permitted to resolve review reports."
            });
        }

        var result = await _reviewService.ResolveReportAsync(
            reportId,
            _callerContext.ActorId.Value,
            request.Resolution,
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
