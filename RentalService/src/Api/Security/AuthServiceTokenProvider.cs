#pragma warning disable CA1848, CA1873
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Security;

public sealed class AuthServiceTokenProvider : IServiceTokenProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthServiceTokenProvider> _logger;
    private static readonly ConcurrentDictionary<string, CachedTokenEntry> _cache = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AuthServiceTokenProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AuthServiceTokenProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<string>> GetServiceTokenAsync(
        string audience,
        string scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            return Error.Validation("INVALID_AUDIENCE", "Audience là bắt buộc để lấy service token.");
        }

        string cacheKey = $"{audience.Trim().ToLowerInvariant()}::{scope.Trim().ToLowerInvariant()}";
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(cacheKey, out var entry) && utcNow < entry.ExpiresAtUtc.AddSeconds(-30))
        {
            return Result.Success(entry.AccessToken);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(cacheKey, out entry) && utcNow < entry.ExpiresAtUtc.AddSeconds(-30))
            {
                return Result.Success(entry.AccessToken);
            }

            string? authBaseUrl = _configuration["Services:Auth:BaseUrl"] ?? _configuration["Authentication:AuthServiceBaseUrl"];
            string? clientId = _configuration["Authentication:ServiceCredentials:ClientId"] ?? "rental-service";
            string? clientSecret = _configuration["Authentication:ServiceCredentials:ClientSecret"];

            if (string.IsNullOrWhiteSpace(authBaseUrl) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError("Thiếu cấu hình AuthService BaseUrl hoặc ClientSecret cho service token");
                return Error.Unauthorized("AUTH_CREDENTIALS_MISSING", "Thiếu cấu hình thông tin xác thực để gọi AuthService.");
            }

            string tokenEndpoint = $"{authBaseUrl.TrimEnd('/')}/api/v1/auth/service-token";

            var tokenRequest = new ServiceTokenRequestPayload(
                clientId,
                clientSecret,
                audience.Trim(),
                scope.Trim());

            using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(tokenRequest, JsonOptions), Encoding.UTF8, "application/json")
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var resp = await _httpClient.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Không thể lấy service token từ AuthService (Status: {Status})", resp.StatusCode);
                return Error.Unauthorized("AUTH_TOKEN_ACQUISITION_FAILED", $"Lấy service token thất bại từ AuthService (Status: {(int)resp.StatusCode}).");
            }

            string json = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            var tokenResp = JsonSerializer.Deserialize<ServiceTokenResponsePayload>(json, JsonOptions);

            if (tokenResp == null || string.IsNullOrWhiteSpace(tokenResp.AccessToken))
            {
                return Error.Failure("AUTH_TOKEN_EMPTY", "Phản hồi từ AuthService không chứa AccessToken hợp lệ.");
            }

            if (!tokenResp.ExpiresAtUtc.HasValue && !tokenResp.ExpiresIn.HasValue)
            {
                return Error.Failure("AUTH_EXPIRY_MISSING", "AuthService không trả về hạn dùng token hợp lệ (expiresAtUtc hoặc expiresIn).");
            }

            DateTimeOffset expiresAt = tokenResp.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddSeconds(tokenResp.ExpiresIn!.Value);

            _cache[cacheKey] = new CachedTokenEntry(tokenResp.AccessToken, expiresAt);
            return Result.Success(tokenResp.AccessToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout khi kết nối tới AuthService lấy service token");
            return Error.ServiceUnavailable("AUTH_TIMEOUT", "Kết nối tới AuthService bị timeout sau 10s.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi gọi AuthService lấy service token");
            return Error.ServiceUnavailable("AUTH_UNAVAILABLE", "Không thể kết nối tới AuthService.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private sealed record CachedTokenEntry(string AccessToken, DateTimeOffset ExpiresAtUtc);

    private sealed record ServiceTokenRequestPayload(
        string ClientId,
        string ClientSecret,
        string Audience,
        string Scope);

    private sealed record ServiceTokenResponsePayload(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
        [property: JsonPropertyName("expiresIn")] int? ExpiresIn);
}
