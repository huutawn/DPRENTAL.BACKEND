using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Dprental.CatalogService.Common.Security;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dprental.CatalogService.Api.Clients;

/// <summary>
/// HTTP implementation của IInventoryServiceClient, giao tiếp với InventoryService qua HTTP nội bộ.
/// </summary>
public sealed partial class InventoryServiceClient : IInventoryServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ICorrelationIdProvider? _correlationIdProvider;
    private readonly ILogger<InventoryServiceClient> _logger;

    public InventoryServiceClient(
        HttpClient httpClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<InventoryServiceClient> logger,
        ICorrelationIdProvider? correlationIdProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _correlationIdProvider = correlationIdProvider;
    }

    /// <inheritdoc />
    public async Task<Result<InventoryListingStatisticsDto>> GetListingStatisticsAsync(
        string scope,
        string? listingStatus,
        CancellationToken cancellationToken = default)
    {
        bool isMine = string.Equals(scope, "mine", StringComparison.OrdinalIgnoreCase);
        string relativeUri = isMine
            ? (!string.IsNullOrWhiteSpace(listingStatus)
                ? $"api/v1/inventory/equipment-listings/statistics?status={Uri.EscapeDataString(listingStatus.Trim())}"
                : "api/v1/inventory/equipment-listings/statistics")
            : "api/v1/inventory/public/equipment-listings/statistics";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);

            if (_correlationIdProvider != null)
            {
                var correlationId = _correlationIdProvider.GetCorrelationId();
                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                }
            }

            if (isMine)
            {
                var httpContext = _httpContextAccessor.HttpContext;
                if (httpContext == null || httpContext.User?.Identity?.IsAuthenticated != true)
                {
                    return Error.Unauthorized(
                        CatalogErrorCode.Inventory.Unauthorized,
                        "Yêu cầu đăng nhập để truy cập scope=mine.");
                }

                if (httpContext.User.HasClaim(CatalogClaims.Types.TokenUse, CatalogClaims.TokenUses.Service))
                {
                    return Error.Forbidden(
                        CatalogErrorCode.Inventory.Forbidden,
                        "Service principal không được phép truy cập scope=mine.");
                }

                var authentication = httpContext.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
                string? token = authentication is { Succeeded: true }
                    ? authentication.Properties?.GetTokenValue("access_token")
                    : null;

                if (string.IsNullOrWhiteSpace(token))
                {
                    return Error.Unauthorized(
                        CatalogErrorCode.Inventory.Unauthorized,
                        "Thiếu access token hợp lệ cho scope=mine.");
                }

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return Error.Unavailable(
                        CatalogErrorCode.Inventory.ServiceUnavailable,
                        "InventoryService trả về phản hồi rỗng.");
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(content);
                }
                catch (JsonException ex)
                {
                    LogConnectionError(_logger, ex);
                    return Error.Unavailable(
                        CatalogErrorCode.Inventory.ServiceUnavailable,
                        "Phản hồi từ InventoryService không đúng định dạng JSON.");
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return Error.Unavailable(
                            CatalogErrorCode.Inventory.ServiceUnavailable,
                            "Phản hồi từ InventoryService phải là JSON object.");
                    }

                    if (!root.TryGetProperty("byEquipmentType", out var eqTypeProp) || eqTypeProp.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("byBrand", out var brandProp) || brandProp.ValueKind != JsonValueKind.Object)
                    {
                        return Error.Unavailable(
                            CatalogErrorCode.Inventory.ServiceUnavailable,
                            "Phản hồi từ InventoryService thiếu dictionary thống kê bắt buộc.");
                    }

                    var byEquipmentType = new Dictionary<Guid, int>();
                    foreach (var prop in eqTypeProp.EnumerateObject())
                    {
                        if (!Guid.TryParse(prop.Name, out var id) || id == Guid.Empty)
                        {
                            return Error.Unavailable(
                                CatalogErrorCode.Inventory.ServiceUnavailable,
                                "EquipmentTypeId trong phản hồi thống kê không hợp lệ.");
                        }

                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var count) || count < 0)
                        {
                            return Error.Unavailable(
                                CatalogErrorCode.Inventory.ServiceUnavailable,
                                "Số đếm listing theo type không được là số âm.");
                        }

                        byEquipmentType[id] = count;
                    }

                    var byBrand = new Dictionary<Guid, int>();
                    foreach (var prop in brandProp.EnumerateObject())
                    {
                        if (!Guid.TryParse(prop.Name, out var id) || id == Guid.Empty)
                        {
                            return Error.Unavailable(
                                CatalogErrorCode.Inventory.ServiceUnavailable,
                                "BrandId trong phản hồi thống kê không hợp lệ.");
                        }

                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var count) || count < 0)
                        {
                            return Error.Unavailable(
                                CatalogErrorCode.Inventory.ServiceUnavailable,
                                "Số đếm listing theo brand không được là số âm.");
                        }

                        byBrand[id] = count;
                    }

                    return Result.Success(new InventoryListingStatisticsDto(byEquipmentType, byBrand));
                }
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Error.Unauthorized(
                    CatalogErrorCode.Inventory.Unauthorized,
                    "Yêu cầu không được xác thực bởi InventoryService.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return Error.Forbidden(
                    CatalogErrorCode.Inventory.Forbidden,
                    "Truy cập bị từ chối bởi InventoryService.");
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return Error.Validation(
                    CatalogErrorCode.Catalog.InvalidRequest,
                    "Tham số thống kê không hợp lệ với InventoryService.");
            }

            return Error.Unavailable(
                CatalogErrorCode.Inventory.ServiceUnavailable,
                $"InventoryService phản hồi mã lỗi {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error.Unavailable(
                CatalogErrorCode.Inventory.ServiceUnavailable,
                "Hết thời gian chờ phản hồi từ InventoryService.");
        }
        catch (HttpRequestException ex)
        {
            LogConnectionError(_logger, ex);
            return Error.Unavailable(
                CatalogErrorCode.Inventory.ServiceUnavailable,
                "Không thể kết nối tới InventoryService.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lỗi kết nối tới InventoryService khi đọc thống kê.")]
    private static partial void LogConnectionError(ILogger logger, Exception ex);
}
