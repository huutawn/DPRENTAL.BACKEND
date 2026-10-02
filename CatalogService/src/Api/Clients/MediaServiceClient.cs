using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.DTOs.Media;
using Dprental.CatalogService.Common;
using Microsoft.Extensions.Logging;

namespace Dprental.CatalogService.Api.Clients;

/// <summary>
/// HTTP implementation của IMediaServiceClient, giao tiếp với MediaService qua HTTP nội bộ.
/// </summary>
public sealed partial class MediaServiceClient : IMediaServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MediaServiceClient> _logger;

    public MediaServiceClient(HttpClient httpClient, ILogger<MediaServiceClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"/api/v1/media/{mediaId:D}/verify", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<MediaVerificationDto>(cancellationToken: cancellationToken);
                if (dto is null)
                {
                    return Result.Failure<MediaVerificationDto>(
                        Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Phản hồi xác minh media không hợp lệ từ MediaService."));
                }

                return Result.Success(dto);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<MediaVerificationDto>(
                    Error.Validation(CatalogErrorCode.Media.NotFound, $"Media {mediaId:D} không tồn tại trên MediaService."));
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                return Result.Failure<MediaVerificationDto>(
                    Error.Validation(CatalogErrorCode.Media.NotReady, $"Media {mediaId:D} không hợp lệ hoặc chưa sẵn sàng."));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LogApiKeyRejected(_logger, (int)response.StatusCode);
                return Result.Failure<MediaVerificationDto>(
                    Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Khóa API nội bộ của MediaService bị từ chối."));
            }

            return Result.Failure<MediaVerificationDto>(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, $"MediaService phản hồi mã lỗi {(int)response.StatusCode}."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            LogVerifyTimeout(_logger, ex, mediaId);
            return Result.Failure<MediaVerificationDto>(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Hết thời gian chờ phản hồi từ MediaService."));
        }
        catch (HttpRequestException ex)
        {
            LogVerifyConnectionError(_logger, ex, mediaId);
            return Result.Failure<MediaVerificationDto>(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Không thể kết nối tới MediaService."));
        }
        catch (JsonException ex)
        {
            LogVerifyJsonError(_logger, ex, mediaId);
            return Result.Failure<MediaVerificationDto>(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Phản hồi không hợp lệ từ MediaService."));
        }
        catch (Exception ex)
        {
            LogVerifyUnexpectedError(_logger, ex, mediaId);
            return Result.Failure<MediaVerificationDto>(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Lỗi hệ thống khi kết nối tới MediaService."));
        }
    }

    public async Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = JsonContent.Create(new { visibility });
            using var response = await _httpClient.PutAsync($"/api/v1/media/{mediaId:D}/visibility", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LogVisibilityApiKeyRejected(_logger, (int)response.StatusCode);
                return Result.Failure(
                    Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Khóa API nội bộ của MediaService bị từ chối."));
            }

            return Result.Failure(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, $"MediaService không thể cập nhật visibility (HTTP {(int)response.StatusCode})."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            LogVisibilityTimeout(_logger, ex, mediaId);
            return Result.Failure(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Hết thời gian chờ phản hồi từ MediaService."));
        }
        catch (HttpRequestException ex)
        {
            LogVisibilityConnectionError(_logger, ex, mediaId);
            return Result.Failure(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Không thể kết nối tới MediaService."));
        }
        catch (JsonException ex)
        {
            LogVisibilityJsonError(_logger, ex, mediaId);
            return Result.Failure(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Phản hồi không hợp lệ từ MediaService."));
        }
        catch (Exception ex)
        {
            LogVisibilityUnexpectedError(_logger, ex, mediaId);
            return Result.Failure(
                Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Lỗi hệ thống khi kết nối tới MediaService."));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "MediaService từ chối API key nội bộ (HTTP {StatusCode}).")]
    private static partial void LogApiKeyRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timeout khi gọi MediaService xác minh media {MediaId}.")]
    private static partial void LogVerifyTimeout(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lỗi kết nối tới MediaService khi xác minh media {MediaId}.")]
    private static partial void LogVerifyConnectionError(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Không thể giải mã phản hồi JSON từ MediaService khi xác minh media {MediaId}.")]
    private static partial void LogVerifyJsonError(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lỗi không xác định khi giao tiếp MediaService cho media {MediaId}.")]
    private static partial void LogVerifyUnexpectedError(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "MediaService từ chối API key nội bộ khi đổi visibility (HTTP {StatusCode}).")]
    private static partial void LogVisibilityApiKeyRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timeout khi đổi visibility trên MediaService cho media {MediaId}.")]
    private static partial void LogVisibilityTimeout(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lỗi kết nối tới MediaService khi đổi visibility cho media {MediaId}.")]
    private static partial void LogVisibilityConnectionError(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lỗi JSON khi đổi visibility cho media {MediaId}.")]
    private static partial void LogVisibilityJsonError(ILogger logger, Exception ex, Guid mediaId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lỗi không xác định khi đổi visibility cho media {MediaId}.")]
    private static partial void LogVisibilityUnexpectedError(ILogger logger, Exception ex, Guid mediaId);
}
