using System.Net;
using System.Text.Json;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.IServices;

namespace Dprental.CatalogService.Api.Clients;

/// <summary>
/// Gọi <c>GET api/v1/media/{mediaId}/verify</c> của Media Service và quy phản hồi về
/// <see cref="MediaVerificationStatus"/>. Chỉ đọc trường <c>status</c>; metadata tệp không đi vào Catalog.
/// </summary>
internal sealed partial class MediaVerificationClient(
    HttpClient httpClient,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<MediaVerificationClient> logger) : IMediaVerificationClient
{
    internal const string ApiKeyHeaderName = "X-Api-Key";
    private const string ReadyStatus = "Ready";

    public async Task<MediaVerificationStatus> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"api/v1/media/{mediaId:D}/verify", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(CorrelationIdProvider.HeaderName, correlationIdProvider.GetCorrelationId());

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    VerifyResponse? body = await response.Content.ReadFromJsonAsync<VerifyResponse>(cancellationToken);
                    if (body?.Status is null)
                    {
                        LogUnavailable(logger, mediaId, "phản hồi 200 thiếu trường status");
                        return MediaVerificationStatus.Unavailable;
                    }

                    return string.Equals(body.Status, ReadyStatus, StringComparison.OrdinalIgnoreCase)
                        ? MediaVerificationStatus.Ready
                        : MediaVerificationStatus.NotReady;

                case HttpStatusCode.NotFound:
                    return MediaVerificationStatus.NotFound;

                case HttpStatusCode.Conflict:
                    return MediaVerificationStatus.NotReady;

                default:
                    LogUnavailable(logger, mediaId, $"HTTP {(int)response.StatusCode}");
                    return MediaVerificationStatus.Unavailable;
            }
        }
        catch (HttpRequestException)
        {
            LogUnavailable(logger, mediaId, "lỗi kết nối");
            return MediaVerificationStatus.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout hủy request bằng token nội bộ; request do client hủy thì để exception đi tiếp.
            LogUnavailable(logger, mediaId, "timeout");
            return MediaVerificationStatus.Unavailable;
        }
        catch (JsonException)
        {
            LogUnavailable(logger, mediaId, "phản hồi 200 không phải JSON hợp lệ");
            return MediaVerificationStatus.Unavailable;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không xác minh được media với Media Service. MediaId: {MediaId}, Reason: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, Guid mediaId, string reason);

    private sealed record VerifyResponse(string? Status);
}
