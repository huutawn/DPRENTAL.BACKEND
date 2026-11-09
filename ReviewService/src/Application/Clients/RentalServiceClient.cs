using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.Options;
using Dprental.ReviewService.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.ReviewService.Application.Clients;

/// <summary>
/// Triển khai IRentalServiceClient gọi sang endpoint nội bộ của RentalService.
/// Thực hiện xác thực qua X-Api-Key, áp dụng timeout 5 giây và ánh xạ lỗi chuẩn.
/// </summary>
public sealed class RentalServiceClient : IRentalServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly RentalServiceOptions _options;
    private readonly ILogger<RentalServiceClient>? _logger;

    public RentalServiceClient(
        HttpClient httpClient,
        IOptions<RentalServiceOptions>? options = null,
        ILogger<RentalServiceClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? new RentalServiceOptions();
        _logger = logger;
    }

    public async Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        string relativePath = $"internal/orders/{orderId}/items/{itemId}/review-snapshot";
        Uri requestUri;

        if (_httpClient.BaseAddress != null)
        {
            requestUri = new Uri(relativePath, UriKind.Relative);
        }
        else if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            requestUri = new Uri($"{_options.BaseUrl.TrimEnd('/')}/{relativePath}");
        }
        else
        {
            requestUri = new Uri($"/{relativePath}", UriKind.Relative);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !_httpClient.DefaultRequestHeaders.Contains("X-Api-Key") &&
            !request.Headers.Contains("X-Api-Key"))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }

        int timeoutSeconds = _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 5;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger?.LogWarning("RentalService authentication failed with HTTP {StatusCode} for order {OrderId}, item {ItemId}",
                    (int)response.StatusCode, orderId, itemId);

                return Result.Failure<RentalItemReviewSnapshotDto>(
                    Error.Failure("RENTAL_SERVICE_AUTHENTICATION_FAILED", "Không thể xác thực với dịch vụ RentalService nội bộ qua X-Api-Key."));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                string notFoundContent = await response.Content.ReadAsStringAsync(cancellationToken);
                bool isItemNotFound = false;
                if (!string.IsNullOrWhiteSpace(notFoundContent))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(notFoundContent);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("title", out var titleProp) &&
                            (string.Equals(titleProp.GetString(), "RENTAL_ITEM_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(titleProp.GetString(), "ITEM_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(titleProp.GetString(), "ORDER_NOT_FOUND", StringComparison.OrdinalIgnoreCase)))
                        {
                            isItemNotFound = true;
                        }
                    }
                    catch
                    {
                        // non-JSON
                    }
                }

                if (isItemNotFound)
                {
                    return Result.Failure<RentalItemReviewSnapshotDto>(
                        Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Không tìm thấy mục thuê hoặc mục thuê không thuộc về đơn hàng đã chỉ định."));
                }

                _logger?.LogWarning("RentalService returned generic 404 (missing route) for order {OrderId}, item {ItemId}",
                    orderId, itemId);

                return Result.Failure<RentalItemReviewSnapshotDto>(
                    Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "RentalService trả về mã 404 chung do thiếu route (không có mã nghiệp vụ xác nhận mục thuê)."));
            }
            if (response.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                return Result.Failure<RentalItemReviewSnapshotDto>(
                    Error.Failure("RENTAL_SERVICE_TIMEOUT", "Dịch vụ RentalService không phản hồi yêu cầu snapshot trong thời gian giới hạn (5 giây)."));
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("RentalService returned unexpected HTTP {StatusCode} for order {OrderId}, item {ItemId}",
                    (int)response.StatusCode, orderId, itemId);

                return Result.Failure<RentalItemReviewSnapshotDto>(
                    Error.Failure("RENTAL_SERVICE_UNAVAILABLE", $"Dịch vụ RentalService phản hồi lỗi HTTP {(int)response.StatusCode}."));
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return Result.Failure<RentalItemReviewSnapshotDto>(
                    Error.Failure("RENTAL_SERVICE_INVALID_RESPONSE", "Phản hồi snapshot từ RentalService rỗng."));
            }

            try
            {
                var dto = JsonSerializer.Deserialize<RentalItemReviewSnapshotDto>(content, JsonOptions);
                if (dto == null ||
                    dto.OrderId == Guid.Empty ||
                    dto.ItemId == Guid.Empty ||
                    dto.RenterId == Guid.Empty ||
                    dto.SupplierId == Guid.Empty ||
                    dto.EquipmentListingId == Guid.Empty ||
                    dto.OrderId != orderId ||
                    dto.ItemId != itemId)
                {
                    return Result.Failure<RentalItemReviewSnapshotDto>(
                        Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "Dữ liệu snapshot từ RentalService không đúng định dạng schema hoặc không khớp orderId/itemId đã yêu cầu."));
                }

                return Result.Success(dto);
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to deserialize RentalService snapshot response for order {OrderId}, item {ItemId}",
                    orderId, itemId);

                return Result.Failure<RentalItemReviewSnapshotDto>(
                    Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "Dữ liệu snapshot từ RentalService không đúng định dạng JSON."));
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "RentalService call timed out after {Timeout}s for order {OrderId}, item {ItemId}",
                timeoutSeconds, orderId, itemId);

            return Result.Failure<RentalItemReviewSnapshotDto>(
                Error.Failure("RENTAL_SERVICE_TIMEOUT", "Dịch vụ RentalService không phản hồi yêu cầu snapshot trong thời gian giới hạn (5 giây)."));
        }
        catch (TimeoutException ex)
        {
            _logger?.LogWarning(ex, "RentalService call timed out for order {OrderId}, item {ItemId}",
                orderId, itemId);

            return Result.Failure<RentalItemReviewSnapshotDto>(
                Error.Failure("RENTAL_SERVICE_TIMEOUT", "Dịch vụ RentalService không phản hồi yêu cầu snapshot trong thời gian giới hạn (5 giây)."));
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Network error while calling RentalService for order {OrderId}, item {ItemId}",
                orderId, itemId);

            return Result.Failure<RentalItemReviewSnapshotDto>(
                Error.Failure("RENTAL_SERVICE_UNAVAILABLE", $"Không thể kết nối đến RentalService: {ex.Message}"));
        }
    }

    public async Task<Result<ReconciliationBatchDto>> GetReconciliationBatchAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        string relativePath = $"internal/orders/review-reconciliation?limit={limit}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            relativePath += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        Uri requestUri = _httpClient.BaseAddress != null
            ? new Uri(relativePath, UriKind.Relative)
            : new Uri($"{_options.BaseUrl.TrimEnd('/')}/{relativePath}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !_httpClient.DefaultRequestHeaders.Contains("X-Api-Key") &&
            !request.Headers.Contains("X-Api-Key"))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Result.Failure<ReconciliationBatchDto>(
                        Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "Endpoint đối soát RentalService không tồn tại (404)."));
                }

                return Result.Failure<ReconciliationBatchDto>(
                    Error.Failure("RENTAL_SERVICE_ERROR", $"RentalService trả về mã lỗi HTTP {(int)response.StatusCode}."));
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var batch = JsonSerializer.Deserialize<ReconciliationBatchDto>(content, JsonOptions);
            return batch != null
                ? Result.Success(batch)
                : Result.Failure<ReconciliationBatchDto>(Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "Dữ liệu đối soát không đúng định dạng."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger?.LogError(ex, "Lỗi khi gọi đối soát RentalService với cursor {Cursor}", cursor);
            return Result.Failure<ReconciliationBatchDto>(
                Error.Failure("RENTAL_SERVICE_UNAVAILABLE", "Dịch vụ RentalService đối soát tạm thời không khả dụng."));
        }
    }
}
