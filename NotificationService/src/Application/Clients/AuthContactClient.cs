using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dprental.NotificationService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Clients;

public sealed record AuthContactDto(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("isEmailVerified")] bool IsEmailVerified);

public sealed record AuthContactBatchResponse(
    [property: JsonPropertyName("contacts")] IReadOnlyList<AuthContactDto> Contacts);

public interface IAuthContactClient
{
    Task<Result<IReadOnlyList<AuthContactDto>>> GetUserContactsBatchAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken = default);
}

public sealed class AuthContactClient : IAuthContactClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<AuthContactClient> _logger;

    public AuthContactClient(HttpClient httpClient, IConfiguration configuration, ILogger<AuthContactClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = configuration["InternalAuthentication:ApiKey"] ?? "dprental-internal-dev-key";
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/internal/users/notification-contacts:batch");
            request.Headers.Add("X-Internal-Api-Key", _apiKey);
            request.Content = JsonContent.Create(new { userIds = distinctIds });

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gọi AuthService notification-contacts:batch thất bại với mã {StatusCode}", response.StatusCode);
                return Result.Failure<IReadOnlyList<AuthContactDto>>(
                    Error.Failure("AUTH_CONTACT_ERROR", $"AuthService returned status {response.StatusCode}"));
            }

            var result = await response.Content.ReadFromJsonAsync<AuthContactBatchResponse>(cancellationToken: cancellationToken);
            return Result.Success<IReadOnlyList<AuthContactDto>>(result?.Contacts ?? Array.Empty<AuthContactDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối tới AuthService khi resolve contacts batch.");
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_SERVICE_UNAVAILABLE", "Không thể kết nối tới AuthService để lấy thông tin liên hệ."));
        }
    }
}
