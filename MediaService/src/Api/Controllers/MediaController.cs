using Dprental.MediaService.Api.Security;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common;
using Dprental.MediaService.Application.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.MediaService.Api.Controllers;

/// <summary>
/// Controller xử lý upload file, truy cập và metadata Media.
/// Tất cả endpoint đều sử dụng tiền tố chuẩn hóa: /api/v1/media.
/// </summary>
[ApiController]
[Route("api/v1/media")]
public class MediaController : ControllerBase
{
    private static readonly Action<ILogger, Guid, string, Exception?> LogPrivateGrant =
        LoggerMessage.Define<Guid, string>(LogLevel.Information, new EventId(1, "PrivateMediaRead"),
            "Media read grant: {MediaId} {Result}");
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
    /// Client gửi danh sách file trong một multipart request và nhận kết quả cho từng file.
    /// </summary>
    [HttpPost("uploads")]
    [Authorize]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 25 * 1024 * 1024)]
    [ProducesResponseType<IReadOnlyList<UploadFileResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upload(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromForm] UploadMediaForm form,
        CancellationToken cancellationToken)
    {
        if (form?.Files is null || form.Files.Count == 0)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "MissingFile", detail: "Cần chọn file để tải lên.");
        }
        if (form.Files.Count > UploadMediaRequest.MaximumBatchSize || form.Files.Any(file => file is null) ||
            form.Sha256Checksums is { Count: > 0 } && form.Sha256Checksums.Count != form.Files.Count)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "InvalidUploadBatch",
                detail: "Danh sách file hoặc checksum không hợp lệ.");

        var streams = new List<Stream>(form.Files.Count);
        try
        {
            var requests = new List<UploadMediaRequest>(form.Files.Count);
            for (int index = 0; index < form.Files.Count; index++)
            {
                IFormFile file = form.Files[index];
                Stream stream = file.OpenReadStream();
                streams.Add(stream);
                requests.Add(new UploadMediaRequest(file.FileName, file.ContentType, file.Length, stream,
                    form.Sha256Checksums is { Count: > 0 } ? form.Sha256Checksums[index] : null));
            }

            Result<IReadOnlyList<UploadFileResultDto>> result = await _uploadService.UploadManyAsync(
                _userContext.UserId ?? string.Empty, idempotencyKey, requests, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return ToActionResult(result, StatusCodes.Status200OK);
        }
        finally
        {
            foreach (Stream stream in streams)
                await stream.DisposeAsync();
        }
    }

    /// <summary>
    /// API nội bộ để Business Service kiểm tra MediaId đang Ready và người upload trước khi lưu tham chiếu.
    /// Yêu cầu service API key qua Gateway; AuthService phát token ngắn hạn để MediaService xác thực.
    /// </summary>
    [HttpGet("{mediaId:guid}/verify")]
    [RequireApiKey]
    [ProducesResponseType<MediaVerificationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyMedia(
        [FromRoute] Guid mediaId,
        CancellationToken cancellationToken)
    {
        var result = await _accessService.VerifyMediaAsync(mediaId, cancellationToken);
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>Metadata cho service có quyền service media scope read hoặc toàn cục */*; không cấp quyền đọc binary.</summary>
    [HttpGet("{mediaId:guid}")]
    [RequireApiKey]
    [ProducesResponseType<MediaMetadataDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetadata(Guid mediaId, CancellationToken cancellationToken)
    {
        var result = await _accessService.GetMetadataAsync(mediaId, cancellationToken);
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>Business Service đã kiểm tra quyền người dùng mới yêu cầu URL đọc Private (yêu cầu service media scope read hoặc */*).</summary>
    [HttpPost("{mediaId:guid}/access")]
    [RequireApiKey]
    [ProducesResponseType<MediaReadUrlDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreatePrivateAccess(
        Guid mediaId, [FromBody] MediaAccessRequest request, CancellationToken cancellationToken)
    {
        var result = await _accessService.CreatePrivateReadUrlAsync(mediaId, request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        LogPrivateGrant(_logger, mediaId, result.IsSuccess ? "granted" : result.Error.Code, null);
        return ToActionResult(result, StatusCodes.Status200OK);
    }

    /// <summary>Public URL ổn định; không có variant chuyển hướng tới URL ký ngắn hạn; có variant trả WebP 200 trực tiếp.</summary>
    [AllowAnonymous]
    [HttpGet("{mediaId:guid}/content")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicContent(
        Guid mediaId,
        [FromQuery] string? variant,
        [FromQuery] bool download,
        CancellationToken cancellationToken)
    {
        bool isOriginalVariant = string.Equals(variant?.Trim(), ImageVariantPolicy.Original, StringComparison.OrdinalIgnoreCase);

        if (download && !string.IsNullOrWhiteSpace(variant) && !isOriginalVariant)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "InvalidVariantDownload",
                detail: "download=true chỉ dùng với bản gốc; kết hợp với variant trả 400.");
        }

        if (download || isOriginalVariant)
        {
            var origResult = await _accessService.CreatePublicReadUrlAsync(mediaId, download, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            LogPublicGrant(_logger, mediaId, origResult.IsSuccess ? "granted" : origResult.Error.Code, null);
            return origResult.IsSuccess
                ? Redirect(origResult.Value.Url)
                : ToActionResult(origResult, StatusCodes.Status302Found);
        }

        string? ifNoneMatch = Request.Headers.IfNoneMatch.FirstOrDefault();
        var variantResult = await _accessService.GetPublicVariantContentAsync(mediaId, variant, ifNoneMatch, cancellationToken);
        if (variantResult.IsFailure)
        {
            Response.Headers.CacheControl = "no-store";
            return ToActionResult(variantResult, StatusCodes.Status200OK);
        }

        var content = variantResult.Value;
        if (content.FallbackToOriginal)
        {
            var origResult = await _accessService.CreatePublicReadUrlAsync(mediaId, false, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            LogPublicGrant(_logger, mediaId, origResult.IsSuccess ? "granted" : origResult.Error.Code, null);
            return origResult.IsSuccess
                ? Redirect(origResult.Value.Url)
                : ToActionResult(origResult, StatusCodes.Status302Found);
        }

        string etag = $"\"{content.ChecksumSha256}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=0, s-maxage=300, must-revalidate";

        if (content.IsNotModified)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(content.ContentStream!, content.ContentType);
    }
    /// <summary>Business Service đồng bộ Public/Private theo trạng thái entity nghiệp vụ (yêu cầu service media scope publish hoặc */*).</summary>
    [HttpPut("{mediaId:guid}/visibility")]
    [RequireApiKey]
    [ProducesResponseType<MediaVisibilityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetVisibility(
        Guid mediaId, [FromBody] ChangeMediaVisibilityRequest request, CancellationToken cancellationToken)
    {
        var result = await _accessService.SetVisibilityAsync(mediaId, request, cancellationToken);
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

/// <summary>Transport multipart của upload; metadata file được đọc từ IFormFile.</summary>
public sealed class UploadMediaForm
{
    public List<IFormFile> Files { get; set; } = [];
    public List<string>? Sha256Checksums { get; set; }
}
