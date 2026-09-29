using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Common;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Api.Clients;

/// <summary>Triển khai HTTP client giao tiếp với MediaService qua mạng nội bộ.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Acceptable for client integration.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1873:Avoid conditional evaluation", Justification = "Acceptable for client integration.")]
internal sealed class MediaServiceClient(
    HttpClient httpClient,
    ILogger<MediaServiceClient> logger) : IMediaServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<Result<MediaVerificationDto>> VerifyMediaAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                $"api/v1/media/{mediaId:D}/verify",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                MediaVerificationDto? dto = await response.Content.ReadFromJsonAsync<MediaVerificationDto>(
                    JsonOptions,
                    cancellationToken);

                return dto is not null && dto.MediaId == mediaId
                    ? Result.Success(dto)
                    : Result.Failure<MediaVerificationDto>(AuthErrors.Media.ServiceUnavailable);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<MediaVerificationDto>(AuthErrors.Media.NotFound);
            }

            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
            {
                return Result.Failure<MediaVerificationDto>(AuthErrors.Media.NotReady);
            }

            logger.LogWarning("MediaService verify returned unexpected status code {StatusCode} for media {MediaId}",
                response.StatusCode, mediaId);
            return Result.Failure<MediaVerificationDto>(AuthErrors.Media.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogError(ex, "HTTP call to MediaService verify failed for media {MediaId}", mediaId);
            return Result.Failure<MediaVerificationDto>(AuthErrors.Media.ServiceUnavailable);
        }
    }

    public async Task<Result<MediaReadUrlDto>> CreatePrivateAccessUrlAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new MediaAccessRequest("read", Download: false);
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                $"api/v1/media/{mediaId:D}/access",
                request,
                JsonOptions,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                MediaReadUrlDto? dto = await response.Content.ReadFromJsonAsync<MediaReadUrlDto>(
                    JsonOptions,
                    cancellationToken);

                return dto is not null && dto.MediaId == mediaId
                    ? Result.Success(dto)
                    : Result.Failure<MediaReadUrlDto>(AuthErrors.Media.ServiceUnavailable);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<MediaReadUrlDto>(AuthErrors.Media.NotFound);
            }

            logger.LogWarning("MediaService access returned unexpected status code {StatusCode} for media {MediaId}",
                response.StatusCode, mediaId);
            return Result.Failure<MediaReadUrlDto>(AuthErrors.Media.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogError(ex, "HTTP call to MediaService access failed for media {MediaId}", mediaId);
            return Result.Failure<MediaReadUrlDto>(AuthErrors.Media.ServiceUnavailable);
        }
    }

    public async Task<Result<MediaVisibilityDto>> SetVisibilityAsync(
        Guid mediaId,
        string visibility,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new ChangeMediaVisibilityRequest(visibility);
            using HttpResponseMessage response = await httpClient.PutAsJsonAsync(
                $"api/v1/media/{mediaId:D}/visibility",
                request,
                JsonOptions,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                MediaVisibilityDto? dto = await response.Content.ReadFromJsonAsync<MediaVisibilityDto>(
                    JsonOptions,
                    cancellationToken);

                return dto is not null && dto.MediaId == mediaId
                    ? Result.Success(dto)
                    : Result.Failure<MediaVisibilityDto>(AuthErrors.Media.ServiceUnavailable);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<MediaVisibilityDto>(AuthErrors.Media.NotFound);
            }

            logger.LogWarning("MediaService visibility returned unexpected status code {StatusCode} for media {MediaId}",
                response.StatusCode, mediaId);
            return Result.Failure<MediaVisibilityDto>(AuthErrors.Media.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogError(ex, "HTTP call to MediaService visibility failed for media {MediaId}", mediaId);
            return Result.Failure<MediaVisibilityDto>(AuthErrors.Media.ServiceUnavailable);
        }
    }
}
