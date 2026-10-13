using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Clients;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Clients;

/// <summary>
/// HTTP client gọi tới MediaService nội bộ bằng API key.
/// </summary>
public sealed class MediaServiceClient : IMediaServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Action<ILogger, string, Exception?> LogJsonError =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "MediaServiceJsonError"), "Lỗi deserialize response từ MediaService khi {Action}.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogTimeout =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(2, "MediaServiceTimeout"), "Timeout khi gọi MediaService cho media {MediaId} khi {Action}.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogNetworkError =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(3, "MediaServiceNetworkError"), "Lỗi kết nối tới MediaService cho media {MediaId} khi {Action}.");

    private readonly HttpClient _httpClient;
    private readonly MediaServiceOptions _options;
    private readonly ILogger<MediaServiceClient>? _logger;

    public MediaServiceClient(
        HttpClient httpClient,
        IOptions<MediaServiceOptions> options,
        ILogger<MediaServiceClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("MediaServiceOptions không được null.", nameof(options));
        _logger = logger;
    }

    private sealed record RemoteMediaVerificationResponse(
        Guid MediaId,
        string Status,
        string UploadedByUserId,
        string? ContentType
    );

    private sealed record RemoteMediaVisibilityResponse(
        Guid MediaId,
        string Visibility
    );

    /// <inheritdoc />
    public async Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        string relativeUri = $"api/v1/media/{mediaId:D}/verify";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "MediaService trả về nội dung rỗng khi verify.");
                }

                RemoteMediaVerificationResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<RemoteMediaVerificationResponse>(content, JsonOptions);
                }
                catch (JsonException ex)
                {
                    if (_logger is not null) LogJsonError(_logger, "verify", ex);
                    return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Phản hồi verify từ MediaService không đúng định dạng JSON.");
                }

                if (parsed is null || parsed.MediaId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(parsed.Status) ||
                    string.IsNullOrWhiteSpace(parsed.UploadedByUserId) ||
                    string.IsNullOrWhiteSpace(parsed.ContentType))
                {
                    return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Phản hồi verify từ MediaService thiếu các trường dữ liệu bắt buộc.");
                }

                return Result.Success(new MediaVerificationInfo(parsed.MediaId, parsed.Status, parsed.UploadedByUserId, parsed.ContentType));
            }

            return MapHttpError(response.StatusCode, "verify");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger is not null) LogTimeout(_logger, mediaId, "verify", ex);
            return Error.Unavailable("MEDIA_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ MediaService.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger is not null) LogNetworkError(_logger, mediaId, "verify", ex);
            return Error.Unavailable("MEDIA_SERVICE_NETWORK_ERROR", "Không thể kết nối tới MediaService.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        string relativeUri = $"api/v1/media/{mediaId:D}/visibility";
        var requestBody = new { visibility = "public" };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, relativeUri)
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "MediaService trả về nội dung rỗng khi cập nhật visibility.");
                }

                RemoteMediaVisibilityResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<RemoteMediaVisibilityResponse>(content, JsonOptions);
                }
                catch (JsonException ex)
                {
                    if (_logger is not null) LogJsonError(_logger, "visibility", ex);
                    return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Phản hồi visibility từ MediaService không đúng định dạng JSON.");
                }

                if (parsed is null || parsed.MediaId != mediaId ||
                    !string.Equals(parsed.Visibility, "public", StringComparison.OrdinalIgnoreCase))
                {
                    return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Phản hồi visibility từ MediaService không xác nhận trạng thái Public hoặc sai ID.");
                }

                return Result.Success(new MediaVisibilityInfo(parsed.MediaId, "Public"));
            }

            return MapHttpError(response.StatusCode, "visibility");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger is not null) LogTimeout(_logger, mediaId, "visibility", ex);
            return Error.Unavailable("MEDIA_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ MediaService.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger is not null) LogNetworkError(_logger, mediaId, "visibility", ex);
            return Error.Unavailable("MEDIA_SERVICE_NETWORK_ERROR", "Không thể kết nối tới MediaService.");
        }
    }

    private static Error MapHttpError(HttpStatusCode statusCode, string action)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound =>
                Error.Validation("MEDIA_NOT_FOUND", "Media không tồn tại hoặc ID không hợp lệ."),

            HttpStatusCode.Conflict =>
                Error.Conflict("MEDIA_STATE_CONFLICT", "Trạng thái media chưa sẵn sàng hoặc xung đột."),

            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Error.Unavailable("MEDIA_SERVICE_AUTH_ERROR", "Dịch vụ Media từ chối xác thực API key nội bộ."),

            HttpStatusCode.TooManyRequests =>
                Error.Unavailable("MEDIA_SERVICE_RATE_LIMITED", "Dịch vụ Media đang quá tải."),

            _ => Error.Unavailable("MEDIA_SERVICE_UNAVAILABLE", $"Dịch vụ Media phản hồi mã lỗi không mong đợi ({statusCode}) khi {action}.")
        };
    }
}
