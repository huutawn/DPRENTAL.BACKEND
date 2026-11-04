using System.Net.Http.Json;
using Dprental.NotificationService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Clients;

public sealed record AuthContactDto(
    Guid UserId,
    string Email,
    bool IsActive,
    bool IsEmailVerified);

public sealed record AuthContactBatchResponse(
    IReadOnlyList<AuthContactDto> Contacts);

public interface IAuthContactClient
{
    Task<Result<IReadOnlyList<AuthContactDto>>> GetUserContactsBatchAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken = default);
}

public sealed class AuthContactClient : IAuthContactClient
{
    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string BatchEndpoint = "/api/v1/auth/internal/users/notification-contacts:batch";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<AuthContactClient> _logger;

    public AuthContactClient(HttpClient httpClient, IConfiguration configuration, ILogger<AuthContactClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var apiKey = configuration["InternalAuthentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Thiếu cấu hình InternalAuthentication:ApiKey.");
        }
        _apiKey = apiKey;

        try
        {
            _httpClient.Timeout = RequestTimeout;
        }
        catch (InvalidOperationException)
        {
            // Ignore if HttpClient has already started sending requests
        }
    }

    public async Task<Result<IReadOnlyList<AuthContactDto>>> GetUserContactsBatchAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        if (userIds == null || userIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<AuthContactDto>>(Array.Empty<AuthContactDto>());
        }

        var distinctIds = userIds.Distinct().Take(100).ToList();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BatchEndpoint);
            request.Headers.Add(ApiKeyHeaderName, _apiKey);
            request.Content = JsonContent.Create(new { userIds = distinctIds });

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.LogWarning("Gọi AuthService notification-contacts:batch thất bại với mã {StatusCode}", response.StatusCode);

                if (statusCode >= 500)
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Failure("AUTH_SERVICE_5XX", $"AuthService returned server error {response.StatusCode} ({statusCode})."));
                }

                if (statusCode >= 400)
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Failure("AUTH_SERVICE_4XX", $"AuthService returned client error {response.StatusCode} ({statusCode})."));
                }

                return Result.Failure<IReadOnlyList<AuthContactDto>>(
                    Error.Failure("AUTH_CONTACT_ERROR", $"AuthService returned status {response.StatusCode} ({statusCode})."));
            }

            var result = await response.Content.ReadFromJsonAsync<AuthContactBatchResponse>(cancellationToken: timeoutCts.Token);
            return Result.Success<IReadOnlyList<AuthContactDto>>(result?.Contacts ?? Array.Empty<AuthContactDto>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Timeout khi kết nối tới AuthService sau {TimeoutSeconds}s.", RequestTimeout.TotalSeconds);
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_NETWORK_TIMEOUT", $"Yêu cầu tới AuthService bị timeout sau {RequestTimeout.TotalSeconds} giây."));
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Timeout khi kết nối tới AuthService sau {TimeoutSeconds}s.", RequestTimeout.TotalSeconds);
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_NETWORK_TIMEOUT", $"Yêu cầu tới AuthService bị timeout sau {RequestTimeout.TotalSeconds} giây."));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Lỗi mạng khi kết nối tới AuthService: {Message}", ex.Message);
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_NETWORK_ERROR", $"Lỗi mạng khi kết nối tới AuthService: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối tới AuthService khi resolve contacts batch.");
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_SERVICE_UNAVAILABLE", "Không thể kết nối tới AuthService để lấy thông tin liên hệ."));
        }
    }
}
