using Dprental.MediaService.Api.Security;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.MediaService.Api.Controllers;

/// <summary>
/// Controller xử lý upload file, truy cập và metadata Media.
/// Tất cả endpoint đều sử dụng tiền tố chuẩn hóa: /api/v1/media.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/media")]
public class MediaController : ControllerBase
{
    private static readonly Action<ILogger, Guid, string?, string, Exception?> LogPrivateGrant =
        LoggerMessage.Define<Guid, string?, string>(LogLevel.Information, new EventId(1, "PrivateMediaRead"),
            "Media read grant: {MediaId} {ServiceId} {Result}");
    private static readonly Action<ILogger, Guid, string, Exception?> LogPublicGrant =
        LoggerMessage.Define<Guid, string>(LogLevel.Information, new EventId(2, "PublicMediaRead"),
            "Public media read grant: {MediaId} {Result}");

    private readonly IMediaUploadService _uploadService;
    private readonly IMediaAccessService _accessService;
    private readonly ICurrentUserContext _userContext;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        IMediaUploadService uploadService,
        IMediaAccessService accessService,
        ICurrentUserContext userContext,
        ILogger<MediaController> logger)
    {
        _uploadService = uploadService ?? throw new ArgumentNullException(nameof(uploadService));
        _accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Client gửi file trong một multipart request và nhận MediaId khi file đã Ready.
    /// </summary>
    [HttpPost("uploads")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 25 * 1024 * 1024)]
    [ProducesResponseType<MediaCompletedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Upload(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromForm] UploadMediaForm form,
        CancellationToken cancellationToken)
    {
        if (form?.File is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "MissingFile", detail: "Cần chọn file để tải lên.");
        }

        await using Stream content = form.File.OpenReadStream();
        var result = await _uploadService.UploadAsync(
            _userContext.UserId ?? string.Empty,
            idempotencyKey,
            new UploadMediaRequest(
                form.File.FileName,
                form.File.ContentType,
                form.File.Length,
                content,
                form.Sha256Checksum),
            cancellationToken);

        Response.Headers.CacheControl = "no-store";
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>
    /// API nội bộ để Business Service kiểm tra MediaId đang Ready và người upload trước khi lưu tham chiếu.
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
        var result = await _accessService.VerifyMediaAsync(
            CurrentService(),
            mediaId,
            cancellationToken);

        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>Metadata cho service có scope media.read; không cấp quyền đọc binary.</summary>
    [HttpGet("{mediaId:guid}")]
    [ProducesResponseType<MediaMetadataDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMetadata(Guid mediaId, CancellationToken cancellationToken)
    {
        var result = await _accessService.GetMetadataAsync(CurrentService(), mediaId, cancellationToken);
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>Business Service đã kiểm tra quyền người dùng mới yêu cầu URL đọc Private.</summary>
    [HttpPost("{mediaId:guid}/access")]
    [ProducesResponseType<MediaReadUrlDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreatePrivateAccess(
        Guid mediaId, [FromBody] MediaAccessRequest request, CancellationToken cancellationToken)
    {
        var result = await _accessService.CreatePrivateReadUrlAsync(CurrentService(), mediaId, request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        LogPrivateGrant(_logger, mediaId, _userContext.ServiceId,
            result.IsSuccess ? "granted" : result.Error.Code, null);
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>Public URL ổn định; chỉ Media Ready và Public được chuyển hướng tới URL ký ngắn hạn.</summary>
    [AllowAnonymous]
    [HttpGet("{mediaId:guid}/content")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> GetPublicContent(
        Guid mediaId, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        var result = await _accessService.CreatePublicReadUrlAsync(mediaId, download, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        LogPublicGrant(_logger, mediaId, result.IsSuccess ? "granted" : result.Error.Code, null);
        return result.IsSuccess
            ? Redirect(result.Value.Url)
            : ToActionResult(result, StatusCodes.Status302Found);
    }

    /// <summary>Business Service đồng bộ Public/Private theo trạng thái entity nghiệp vụ.</summary>
    [HttpPut("{mediaId:guid}/visibility")]
    [ProducesResponseType<MediaVisibilityDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetVisibility(
        Guid mediaId, [FromBody] ChangeMediaVisibilityRequest request, CancellationToken cancellationToken)
    {
        var result = await _accessService.SetVisibilityAsync(CurrentService(), mediaId, request, cancellationToken);
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    private ServiceCaller CurrentService() =>
        new(_userContext.ServiceId, _userContext.ServiceScopes);

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

/// <summary>Transport multipart của upload; metadata file được đọc từ IFormFile.</summary>
public sealed class UploadMediaForm
{
    public IFormFile? File { get; set; }
    public string? Sha256Checksum { get; set; }
}
