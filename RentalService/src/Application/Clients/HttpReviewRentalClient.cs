#pragma warning disable CA1848, CA1873
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Reviews;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Implementation HTTP client gọi endpoint batch trạng thái review của ReviewService.
/// </summary>
public sealed class HttpReviewRentalClient : IReviewRentalClient
{
    private const string BatchStatusRoute = "/api/v1/reviews/status/batch";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpReviewRentalClient> _logger;
    private readonly string _baseUrl;

    public HttpReviewRentalClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<HttpReviewRentalClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string configuredBase = configuration["Services:Review:BaseUrl"] ?? "http://review-service:8080";
        _baseUrl = configuredBase.TrimEnd('/');
    }

    public async Task<Result<BatchReviewStatusResponse>> GetBatchReviewStatusAsync(
        BatchReviewStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string fullUrl = $"{_baseUrl}{BatchStatusRoute}";

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            string jsonBody = JsonSerializer.Serialize(request, JsonOptions);
            httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ReviewService batch status trả về HTTP {StatusCode}",
                    (int)response.StatusCode);

                return Result.Failure<BatchReviewStatusResponse>(
                    Error.Failure(
                        "REVIEW_SERVICE_ERROR",
                        $"ReviewService trả về mã lỗi HTTP {(int)response.StatusCode}."));
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<BatchReviewStatusResponse>(responseJson, JsonOptions);

            if (result is null || result.Items is null)
            {
                _logger.LogWarning("ReviewService trả về body batch status không hợp lệ hoặc rỗng");
                return Result.Failure<BatchReviewStatusResponse>(
                    Error.Failure("REVIEW_SERVICE_INVALID_RESPONSE", "Dữ liệu trạng thái review không đúng định dạng."));
            }

            return Result.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ReviewService trả về JSON không đúng định dạng tại {Url}", fullUrl);
            return Result.Failure<BatchReviewStatusResponse>(
                Error.Failure("REVIEW_SERVICE_INVALID_RESPONSE", "Dữ liệu trạng thái review không đúng định dạng."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối ngoại vi khi gọi ReviewService batch status tại {Url}", fullUrl);
            return Result.Failure<BatchReviewStatusResponse>(
                Error.Failure("REVIEW_SERVICE_UNAVAILABLE", "Không thể kết nối tới ReviewService."));
        }
    }
}
