using Dprental.MediaService.Api.Security;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.MediaService.Api.Controllers;

/// <summary>
/// Controller xử lý các yêu cầu tải tệp media trực tiếp lên MinIO và quản lý metadata.
/// Tất cả endpoint đều sử dụng tiền tố chuẩn hóa: /api/v1/media.
/// </summary>
[ApiController]
[Route("api/v1/media")]
public class MediaController : ControllerBase
{
    private readonly IMediaUploadService _uploadService;
    private readonly ICurrentUserContext _userContext;

    public MediaController(
        IMediaUploadService uploadService,
        ICurrentUserContext userContext)
    {
        _uploadService = uploadService ?? throw new ArgumentNullException(nameof(uploadService));
        _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
    }

    /// <summary>
    /// Client dùng user token để khởi tạo phiên tải tệp trực tiếp lên MinIO.
    /// </summary>
    [HttpPost("uploads")]
    [ProducesResponseType<UploadSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> InitializeUpload(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] InitializeUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_userContext.UserId))
        {
            return Problem(
                detail: "Yêu cầu phải được xác thực từ một người dùng hợp lệ.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "UnauthorizedUser");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Problem(
                detail: "Tiêu đề Idempotency-Key là bắt buộc.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "MissingIdempotencyKey");
        }

        var result = await _uploadService.InitializeUploadAsync(
            _userContext.UserId,
            idempotencyKey,
            request,
            cancellationToken);

        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>
    /// Client xác nhận đã hoàn tất upload binary lên MinIO; MediaService xác minh object và chuyển sang Ready.
    /// </summary>
    [HttpPost("uploads/{uploadId:guid}/complete")]
    [ProducesResponseType<MediaCompletedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CompleteUpload(
        [FromRoute] Guid uploadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_userContext.UserId))
        {
            return Problem(
                detail: "Yêu cầu phải được xác thực từ một người dùng hợp lệ.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "UnauthorizedUser");
        }

        var result = await _uploadService.CompleteUploadAsync(
            _userContext.UserId,
            uploadId,
            cancellationToken);

        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>
    /// API nội bộ để Business Service kiểm tra MediaId đang ở trạng thái Ready, đúng purpose, người upload và đối tượng trước khi lưu tham chiếu.
    /// Yêu cầu Service token đã xác thực.
    /// </summary>
    [HttpGet("{mediaId:guid}/verify")]
    [ProducesResponseType<MediaVerificationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyMedia(
        [FromRoute] Guid mediaId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_userContext.ServiceName))
        {
            return Problem(
                detail: "Yêu cầu phải được xác thực từ một service hợp lệ.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "UnauthorizedService");
        }

        var result = await _uploadService.VerifyMediaAsync(
            _userContext.ServiceName,
            mediaId,
            cancellationToken);

        return ToActionResult(result, StatusCodes.Status200OK);
    }

    private ObjectResult ToActionResult<T>(Result<T> result, int successStatusCode)
    {
        if (result.IsSuccess)
        {
            return StatusCode(successStatusCode, result.Value);
        }

        var error = result.Error;
        int statusCode = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Expired => StatusCodes.Status400BadRequest,
            ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError
        };

        return Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: error.Code);
    }
}
