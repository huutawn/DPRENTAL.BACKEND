using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Media;
using Dprental.ReviewService.Application.Options;
using Dprental.ReviewService.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.ReviewService.Application.Clients;

/// <summary>
/// Triển khai IMediaServiceClient gọi sang MediaService với xác thực qua X-Api-Key.
/// </summary>
public sealed class MediaServiceClient : IMediaServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly MediaServiceOptions _options;
    private readonly ILogger<MediaServiceClient>? _logger;

    public MediaServiceClient(
        HttpClient httpClient,
        IOptions<MediaServiceOptions>? options = null,
        ILogger<MediaServiceClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? new MediaServiceOptions();
        _logger = logger;
    }

    public async Task<Result<MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        string relativePath = $"api/v1/media/{mediaId}/verify";
        var requestUri = BuildUri(relativePath);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        AddHeaders(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<MediaVerifyDto>(
                    Error.NotFound("MEDIA_NOT_FOUND", "Tệp media không tồn tại trên hệ thống."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<MediaVerifyDto>(
                    Error.Failure("MEDIA_SERVICE_ERROR", $"MediaService trả về mã lỗi HTTP {(int)response.StatusCode}."));
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var dto = JsonSerializer.Deserialize<MediaVerifyDto>(content, JsonOptions);

            return dto != null
                ? Result.Success(dto)
                : Result.Failure<MediaVerifyDto>(Error.Failure("MEDIA_CONTRACT_INVALID", "Phản hồi xác minh media không đúng định dạng."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger?.LogError(ex, "Lỗi kết nối khi gọi xác minh media {MediaId}", mediaId);
            return Result.Failure<MediaVerifyDto>(
                Error.Failure("MEDIA_SERVICE_UNAVAILABLE", "Dịch vụ MediaService tạm thời không khả dụng."));
        }
    }

    public async Task<Result<MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        string relativePath = $"api/v1/media/{mediaId}/access?purpose=review";
        var requestUri = BuildUri(relativePath);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        AddHeaders(request);

        var body = new { action = "read", download = false, purpose = "review" };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure<MediaAccessDto>(
                    Error.NotFound("MEDIA_NOT_FOUND", "Tệp media không tồn tại trên hệ thống."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<MediaAccessDto>(
                    Error.Failure("MEDIA_SERVICE_ERROR", $"MediaService trả về mã lỗi HTTP {(int)response.StatusCode}."));
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var dto = JsonSerializer.Deserialize<MediaAccessDto>(content, JsonOptions);

            return dto != null
                ? Result.Success(dto)
                : Result.Failure<MediaAccessDto>(Error.Failure("MEDIA_CONTRACT_INVALID", "Phản hồi cấp quyền truy cập media không đúng định dạng."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger?.LogError(ex, "Lỗi kết nối khi tạo access token cho media {MediaId}", mediaId);
            return Result.Failure<MediaAccessDto>(
                Error.Failure("MEDIA_SERVICE_UNAVAILABLE", "Dịch vụ MediaService tạm thời không khả dụng."));
        }
    }

    private Uri BuildUri(string relativePath)
    {
        if (_httpClient.BaseAddress != null)
        {
            return new Uri(relativePath, UriKind.Relative);
        }
        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return new Uri($"{_options.BaseUrl.TrimEnd('/')}/{relativePath}");
        }
        return new Uri($"/{relativePath}", UriKind.Relative);
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !_httpClient.DefaultRequestHeaders.Contains("X-Api-Key") &&
            !request.Headers.Contains("X-Api-Key"))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }
    }
}
