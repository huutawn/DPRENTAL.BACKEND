using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;

namespace Dprental.RentalService.Application.Clients;

public sealed class HttpRentalMediaClient : IRentalMediaClient
{
    private readonly HttpClient _http;
    private readonly Uri? _baseUri;
    private readonly string? _apiKey;

    public HttpRentalMediaClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        string? baseUrl = configuration["Services:Media:BaseUrl"];
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            _baseUri = uri;
        _apiKey = configuration["MediaService:ApiKey"];
    }

    public Task<Result<MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken ct = default) =>
        SendAsync<MediaVerifyDto>(HttpMethod.Get, MediaContractSpec.Routes.VerifyMedia(mediaId), null, ct);

    public Task<Result<MediaMetadataDto>> GetMetadataAsync(Guid mediaId, CancellationToken ct = default) =>
        SendAsync<MediaMetadataDto>(HttpMethod.Get, MediaContractSpec.Routes.GetMetadata(mediaId), null, ct);

    public Task<Result<MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CreateMediaAccessRequest request, CancellationToken ct = default) =>
        SendAsync<MediaAccessDto>(HttpMethod.Post, MediaContractSpec.Routes.CreatePrivateAccess(mediaId), request, ct);

    private async Task<Result<T>> SendAsync<T>(HttpMethod method, string route, CreateMediaAccessRequest? body, CancellationToken ct)
    {
        if (_baseUri is null || string.IsNullOrWhiteSpace(_apiKey))
            return Error.ServiceUnavailable("MEDIA_NOT_CONFIGURED", "Media verification is not configured.");
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, route));
        request.Headers.Add(MediaContractSpec.AuthHeaderName, _apiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Error.NotFound(MediaContractSpec.ErrorCodes.MediaNotFound, "Evidence media was not found.");
            if (!response.IsSuccessStatusCode)
                return Error.ServiceUnavailable("MEDIA_UNAVAILABLE", "Media verification is temporarily unavailable.");
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return value is null
                ? Error.ServiceUnavailable("MEDIA_INVALID_RESPONSE", "Media returned an invalid response.")
                : Result.Success(value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error.ServiceUnavailable("MEDIA_TIMEOUT", "Media verification timed out.");
        }
        catch (HttpRequestException)
        {
            return Error.ServiceUnavailable("MEDIA_UNAVAILABLE", "Media verification is temporarily unavailable.");
        }
        catch (JsonException)
        {
            return Error.ServiceUnavailable("MEDIA_INVALID_RESPONSE", "Media returned an invalid response.");
        }
    }
}
