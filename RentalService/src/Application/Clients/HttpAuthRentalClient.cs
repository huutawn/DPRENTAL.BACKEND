#pragma warning disable CA1848, CA1873
using System.Net;
using System.Text.Json;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Clients;

public sealed class HttpAuthRentalClient : IAuthRentalClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpAuthRentalClient> _logger;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public HttpAuthRentalClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<HttpAuthRentalClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string configuredBase = configuration["Services:Auth:BaseUrl"] ?? "http://auth-service:8080";
        _baseUrl = configuredBase.TrimEnd('/');

        _apiKey = configuration["InternalAuthentication:ApiKey"]
            ?? configuration["INTERNAL_API_KEY"]
            ?? configuration["Services:Auth:ApiKey"]
            ?? string.Empty;

        _httpClient.Timeout = RequestTimeout;
    }

    public async Task<Result<AuthUserSummaryDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        string endpoint = _baseUrl.EndsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
            ? $"{_baseUrl}/users/me"
            : $"{_baseUrl}/api/v1/auth/users/me";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(_apiKey) && !request.Headers.Contains("X-Api-Key"))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AuthService GET /users/me timed out after {TimeoutSeconds}s.", RequestTimeout.TotalSeconds);
            return CheckoutErrors.DependencyUnavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            _logger.LogWarning(ex, "Network error when calling AuthService GET /users/me.");
            return CheckoutErrors.DependencyUnavailable;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                return CheckoutErrors.Unauthenticated;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                string? errorCode = null;
                string? errorMessage = null;
                try
                {
                    string errContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(errContent);
                    if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    {
                        errorCode = titleProp.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("code", out var codeProp))
                    {
                        errorCode = codeProp.GetString();
                    }

                    if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                    {
                        errorMessage = detailProp.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("message", out var msgProp))
                    {
                        errorMessage = msgProp.GetString();
                    }
                }
                catch
                {
                    // Non-JSON error body
                }

                return Error.Forbidden(
                    errorCode ?? "Auth.Forbidden",
                    errorMessage ?? "Tài khoản không có quyền thực hiện thao tác.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AuthService GET /users/me returned upstream status {StatusCode}.", (int)response.StatusCode);
                return CheckoutErrors.DependencyUnavailable;
            }

            string content;
            try
            {
                content = await response.Content.ReadAsStringAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read AuthService GET /users/me response content.");
                return CheckoutErrors.DependencyUnavailable;
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return CheckoutErrors.DependencyUnavailable;
                }

                if (!root.TryGetProperty("id", out var idProp) &&
                    !root.TryGetProperty("Id", out idProp))
                {
                    return CheckoutErrors.DependencyUnavailable;
                }

                if (idProp.ValueKind != JsonValueKind.String ||
                    !idProp.TryGetGuid(out var userId) || userId == Guid.Empty)
                {
                    return CheckoutErrors.DependencyUnavailable;
                }

                if (!root.TryGetProperty("isProfileCompleted", out var pcProp) &&
                    !root.TryGetProperty("IsProfileCompleted", out pcProp))
                {
                    return CheckoutErrors.DependencyUnavailable;
                }

                if (pcProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return CheckoutErrors.DependencyUnavailable;
                }

                bool isProfileCompleted = pcProp.GetBoolean();
                return new AuthUserSummaryDto(userId, isProfileCompleted);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse AuthService GET /users/me response JSON.");
                return CheckoutErrors.DependencyUnavailable;
            }
        }
    }
}
