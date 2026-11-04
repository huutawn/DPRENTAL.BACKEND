using Dprental.ReviewService.Api.Filters;
using Dprental.ReviewService.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Application.Security;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Dprental.ReviewService.Api.Controllers;

/// <summary>
/// Controller xử lý vòng đời đánh giá và truy vấn chi tiết đánh giá riêng tư (Public and Author API).
/// </summary>
[ApiController]
[Authorize(Policy = JwtAuthenticationExtensions.HumanOnlyPolicy)]
[Route("reviews")]
public sealed class ReviewsController : ControllerBase
{
    private readonly IReviewService _reviewService;
    private readonly IReviewCallerContext _callerContext;

    public ReviewsController(
        IReviewService reviewService,
        IReviewCallerContext callerContext)
    {
        _reviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Tạo mới một đánh giá cho mục thuê đã hoàn tất (POST /api/v1/reviews).
    /// </summary>
    /// <param name="request">Dữ liệu yêu cầu tạo đánh giá.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp từ header Idempotency-Key.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>201 Created với Location và ETag nếu thành công, hoặc ProblemDetails lỗi tương ứng.</returns>
    [HttpPost]
    public async Task<IActionResult> CreateReview(
        [FromBody] CreateReviewRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa được xác thực."));
        }

        if (!_callerContext.ActorId.HasValue || _callerContext.ActorId.Value == Guid.Empty)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ."));
        }

        var result = await _reviewService.CreateReviewAsync(
            _callerContext.ActorId.Value,
            request,
            idempotencyKey ?? string.Empty,
            _callerContext.UserType,
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.ETag = $"\"{result.Value.Version}\"";
        return Created($"/api/v1/reviews/{result.Value.ReviewId}", result.Value);
    }

    /// <summary>
    /// Chỉnh sửa đánh giá đã có trong thời hạn cho phép (PUT /api/v1/reviews/{reviewId}).
    /// </summary>
    /// <param name="reviewId">Mã định danh đánh giá cần chỉnh sửa.</param>
    /// <param name="request">Dữ liệu cập nhật điểm và nội dung bình luận.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp.</param>
    /// <param name="ifMatch">Header If-Match chứa ETag phiên bản hiện tại.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>200 OK với ETag mới nếu thành công, hoặc ProblemDetails lỗi tương ứng.</returns>
    [HttpPut("{reviewId:guid}")]
    public async Task<IActionResult> UpdateReview(
        [FromRoute] Guid reviewId,
        [FromBody] UpdateReviewRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa được xác thực."));
        }

        if (!_callerContext.ActorId.HasValue || _callerContext.ActorId.Value == Guid.Empty)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ."));
        }

        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return this.ToProblem(Error.PreconditionRequired(
                "PRECONDITION_REQUIRED",
                "Yêu cầu chỉnh sửa hoặc thay đổi trạng thái đánh giá bắt buộc phải có header 'If-Match' chứa ETag phiên bản hiện tại."));
        }

        var result = await _reviewService.UpdateReviewAsync(
            _callerContext.ActorId.Value,
            reviewId,
            request,
            idempotencyKey ?? string.Empty,
            ifMatch,
            _callerContext.UserType,
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.ETag = $"\"{result.Value.Version}\"";
        return Ok(result.Value);
    }

    /// <summary>
    /// Tác giả chủ động rút lại đánh giá (POST /api/v1/reviews/{reviewId}/withdraw).
    /// </summary>
    /// <param name="reviewId">Mã định danh đánh giá cần rút.</param>
    /// <param name="request">Lý do rút đánh giá (tùy chọn).</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp.</param>
    /// <param name="ifMatch">Header If-Match chứa ETag phiên bản hiện tại.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>200 OK với ETag mới nếu thành công, hoặc ProblemDetails lỗi tương ứng.</returns>
    [HttpPost("{reviewId:guid}/withdraw")]
    public async Task<IActionResult> WithdrawReview(
        [FromRoute] Guid reviewId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WithdrawReviewRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa được xác thực."));
        }

        if (!_callerContext.ActorId.HasValue || _callerContext.ActorId.Value == Guid.Empty)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ."));
        }

        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return this.ToProblem(Error.PreconditionRequired(
                "PRECONDITION_REQUIRED",
                "Yêu cầu chỉnh sửa hoặc thay đổi trạng thái đánh giá bắt buộc phải có header 'If-Match' chứa ETag phiên bản hiện tại."));
        }

        var result = await _reviewService.WithdrawReviewAsync(
            _callerContext.ActorId.Value,
            reviewId,
            request,
            idempotencyKey ?? string.Empty,
            ifMatch,
            _callerContext.UserType,
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.ETag = $"\"{result.Value.Version}\"";
        return Ok(result.Value);
    }

    /// <summary>
    /// Đọc thông tin chi tiết đánh giá riêng tư (GET /api/v1/reviews/{reviewId}).
    /// </summary>
    /// <param name="reviewId">Mã định danh đánh giá.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>200 OK kèm ETag nếu hợp lệ, 401 nếu chưa đăng nhập, hoặc 404 nếu không có quyền.</returns>
    [HttpGet("{reviewId:guid}")]
    public async Task<IActionResult> GetReviewDetail(
        [FromRoute] Guid reviewId,
        CancellationToken cancellationToken)
    {
        if (!_callerContext.IsAuthenticated)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa được xác thực."));
        }

        if (!_callerContext.ActorId.HasValue || _callerContext.ActorId.Value == Guid.Empty)
        {
            return this.ToProblem(Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ."));
        }

        var result = await _reviewService.GetReviewDetailAsync(
            reviewId,
            _callerContext.ActorId.Value,
            _callerContext.IsAdmin,
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.ETag = $"\"{result.Value.Version}\"";
        return Ok(result.Value);
    }

    /// <summary>
    /// Đọc danh sách đánh giá do người gọi viết (GET /api/v1/reviews/my).
    /// </summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMyReviews(
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
                Detail = "Authentication is required to view your reviews."
            });
        }

        var result = await _reviewService.GetMyReviewsAsync(
            _callerContext.ActorId.Value, page, pageSize, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đọc danh sách đánh giá công khai của một thiết bị (GET /api/v1/reviews/listings/{listingId}).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("listings/{listingId:guid}")]
    public async Task<IActionResult> GetListingReviews(
        [FromRoute] Guid listingId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _reviewService.GetListingReviewsAsync(
            listingId, page, pageSize, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đọc danh sách đánh giá công khai của nhà cung cấp (GET /api/v1/reviews/suppliers/{supplierId}).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("suppliers/{supplierId:guid}")]
    public async Task<IActionResult> GetSupplierReviews(
        [FromRoute] Guid supplierId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _reviewService.GetSupplierReviewsAsync(
            supplierId, page, pageSize, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Xem tổng hợp điểm đánh giá cho listing hoặc supplier (GET /api/v1/reviews/summary).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? targetType,
        [FromQuery] Guid? targetId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetType) || !targetId.HasValue || targetId.Value == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                Title = "Validation.Error",
                Status = StatusCodes.Status400BadRequest,
                Detail = "Both targetType ('listing' or 'supplier') and targetId (non-empty UUID) are required."
            });
        }

        var result = await _reviewService.GetSummaryAsync(
            targetType, targetId.Value, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Lấy danh sách mục thuê đang chờ người dùng đánh giá (GET /api/v1/reviews/pending).
    /// </summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingReviews(
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
                Detail = "Authentication is required to view pending reviews."
            });
        }

        var result = await _reviewService.GetPendingReviewsAsync(
            _callerContext.ActorId.Value, page, pageSize, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đọc danh sách đánh giá nhận được (GET /api/v1/reviews/my/received).
    /// </summary>
    [HttpGet("my/received")]
    public async Task<IActionResult> GetReceivedReviews(
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

        var result = await _reviewService.GetReceivedReviewsAsync(
            _callerContext.ActorId.Value, page, pageSize, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đọc điểm uy tín riêng tư của người thuê (GET /api/v1/reviews/my/reputation).
    /// </summary>
    [HttpGet("my/reputation")]
    public async Task<IActionResult> GetRenterReputation(CancellationToken cancellationToken = default)
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

        var result = await _reviewService.GetRenterReputationAsync(
            _callerContext.ActorId.Value, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Báo cáo vi phạm đánh giá (POST /api/v1/reviews/{reviewId}/reports).
    /// </summary>
    [HttpPost("{reviewId:guid}/reports")]
    public async Task<IActionResult> CreateReport(
        [FromRoute] Guid reviewId,
        [FromBody] CreateReviewReportRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
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

        var result = await _reviewService.CreateReportAsync(
            reviewId,
            _callerContext.ActorId.Value,
            request?.Reason ?? string.Empty,
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đính kèm media vào đánh giá (POST /api/v1/reviews/{reviewId}/media).
    /// </summary>
    [HttpPost("{reviewId:guid}/media")]
    public async Task<IActionResult> AttachMedia(
        [FromRoute] Guid reviewId,
        [FromBody] AttachMediaRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
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

        var result = await _reviewService.AttachMediaAsync(
            reviewId,
            _callerContext.ActorId.Value,
            request.MediaId,
            request.MediaType,
            ifMatch ?? string.Empty,
            cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Gỡ media khỏi đánh giá (DELETE /api/v1/reviews/{reviewId}/media/{mediaId}).
    /// </summary>
    [HttpDelete("{reviewId:guid}/media/{mediaId:guid}")]
    public async Task<IActionResult> DetachMedia(
        [FromRoute] Guid reviewId,
        [FromRoute] Guid mediaId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
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

        var result = await _reviewService.DetachMediaAsync(
            reviewId,
            _callerContext.ActorId.Value,
            mediaId,
            ifMatch ?? string.Empty,
            cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : this.ToProblem(result.Error);
    }
}
