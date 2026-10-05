using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Api.Clients;

/// <summary>
/// HTTP client tích hợp với CatalogService cho việc tra cứu loại thiết bị và thẩm tra spec.
/// </summary>
public sealed class CatalogServiceClient : ICatalogServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Action<ILogger, string, Exception?> LogJsonError =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "CatalogServiceJsonError"), "Lỗi deserialize response từ CatalogService khi {Action}.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogTimeout =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(2, "CatalogServiceTimeout"), "Timeout khi gọi CatalogService cho equipment type {EquipmentTypeId} khi {Action}.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogNetworkError =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(3, "CatalogServiceNetworkError"), "Lỗi kết nối tới CatalogService cho equipment type {EquipmentTypeId} khi {Action}.");

    private readonly HttpClient _httpClient;
    private readonly CatalogServiceOptions _options;
    private readonly ILogger<CatalogServiceClient>? _logger;

    public CatalogServiceClient(
        HttpClient httpClient,
        IOptions<CatalogServiceOptions> options,
        ILogger<CatalogServiceClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("CatalogServiceOptions không được null.", nameof(options));
        _logger = logger;
    }

    private sealed record RemoteEquipmentTypeResponse(
        Guid Id,
        string Code,
        string Name,
        string? Description,
        JsonElement Status,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        DateTime? PublishedAt
    );

    /// <inheritdoc />
    public async Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        if (equipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        string relativeUri = $"api/v1/catalog/equipment-types/{equipmentTypeId:D}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Error.Validation("EQUIPMENT_TYPE_NOT_FOUND", "Loại thiết bị không tồn tại hoặc chưa được công khai.");
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "CatalogService trả về nội dung rỗng.");
                }

                RemoteEquipmentTypeResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<RemoteEquipmentTypeResponse>(content, JsonOptions);
                }
                catch (JsonException ex)
                {
                    if (_logger is not null) LogJsonError(_logger, "get-equipment-type", ex);
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService không đúng định dạng JSON.");
                }

                if (parsed is null || parsed.Id == Guid.Empty || string.IsNullOrWhiteSpace(parsed.Code) || string.IsNullOrWhiteSpace(parsed.Name))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService thiếu các trường dữ liệu bắt buộc.");
                }

                if (parsed.Id != equipmentTypeId)
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "ID của loại thiết bị phản hồi không khớp với yêu cầu.");
                }

                bool isPublished = false;
                bool isKnownUnpublished = false;
                int statusCode = -1;

                if (parsed.Status.ValueKind == JsonValueKind.Number && parsed.Status.TryGetInt32(out var numStatus))
                {
                    statusCode = numStatus;
                    if (numStatus == 1)
                    {
                        isPublished = true;
                    }
                    else if (numStatus is 0 or 2)
                    {
                        isKnownUnpublished = true;
                    }
                }
                else if (parsed.Status.ValueKind == JsonValueKind.String)
                {
                    var strStatus = parsed.Status.GetString();
                    if (string.Equals(strStatus, "Published", StringComparison.OrdinalIgnoreCase))
                    {
                        isPublished = true;
                        statusCode = 1;
                    }
                    else if (string.Equals(strStatus, "Draft", StringComparison.OrdinalIgnoreCase))
                    {
                        isKnownUnpublished = true;
                        statusCode = 0;
                    }
                    else if (string.Equals(strStatus, "Disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        isKnownUnpublished = true;
                        statusCode = 2;
                    }
                }

                if (isKnownUnpublished)
                {
                    return Error.Validation("EQUIPMENT_TYPE_NOT_PUBLISHED", "Loại thiết bị chưa được công khai.");
                }

                if (!isPublished)
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Trạng thái loại thiết bị từ CatalogService không hợp lệ hoặc không thuộc contract.");
                }
                return Result.Success(new CatalogEquipmentTypeResponse(
                    parsed.Id,
                    parsed.Code,
                    parsed.Name,
                    parsed.Description,
                    statusCode,
                    parsed.CreatedAt,
                    parsed.UpdatedAt,
                    parsed.PublishedAt));
            }

            return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger is not null) LogTimeout(_logger, equipmentTypeId, "get-equipment-type", ex);
            return Error.Unavailable("CATALOG_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ CatalogService.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger is not null) LogNetworkError(_logger, equipmentTypeId, "get-equipment-type", ex);
            return Error.Unavailable("CATALOG_SERVICE_NETWORK_ERROR", "Không thể kết nối tới CatalogService.");
        }
    }

    /// <inheritdoc />
    public Task<Result<CatalogSpecValidationResult>> ValidateSpecificationAsync(
        Guid equipmentTypeId,
        int schemaVersion,
        string specValues,
        CancellationToken cancellationToken = default)
    {
        // Theo quyết định MVP đã chốt: CatalogService chưa hỗ trợ canonical spec validation.
        // Trả về rõ ràng NotSupported, không gọi endpoint giả định.
        return Task.FromResult(Result.Success(CatalogSpecValidationResult.NotSupported()));
    }
}
