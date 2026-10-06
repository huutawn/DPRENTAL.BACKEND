using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Clients;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Clients;

/// <summary>
/// HTTP client tích hợp với CatalogService cho việc tra cứu loại thiết bị, thương hiệu và thẩm tra spec.
/// </summary>
public sealed partial class CatalogServiceClient : ICatalogServiceClient
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

    private static readonly Action<ILogger, string, string, Exception?> LogTimeoutResource =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(4, "CatalogServiceResourceTimeout"), "Timeout khi gọi CatalogService cho {Resource} khi {Action}.");

    private static readonly Action<ILogger, string, string, Exception?> LogNetworkErrorResource =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5, "CatalogServiceResourceNetworkError"), "Lỗi kết nối tới CatalogService cho {Resource} khi {Action}.");

    private static readonly HashSet<string> AllowedFieldValidationCodes = new(StringComparer.Ordinal)
    {
        "REQUIRED",
        "TYPE_MISMATCH",
        "OUT_OF_RANGE",
        "INVALID_ENUM_OPTION",
        "UNKNOWN_FIELD"
    };

    private readonly HttpClient _httpClient;
    private readonly CatalogServiceOptions _options;
    private readonly ILogger<CatalogServiceClient>? _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CatalogServiceClient(
        HttpClient httpClient,
        IOptions<CatalogServiceOptions> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CatalogServiceClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("CatalogServiceOptions không được null.", nameof(options));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger;
    }

    private string? GetAccessToken()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return null;
        }

        var authFeature = httpContext.Features.Get<IAuthenticateResultFeature>();
        if (authFeature?.AuthenticateResult is { Succeeded: true } authenticateResult &&
            authenticateResult.Properties is { } props)
        {
            string? token = props.GetTokenValue("access_token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
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

    private sealed record RemoteProblemDetails(
        string? Title,
        string? Detail,
        int? Status
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
    public async Task<Result<CatalogBrandResponse>> GetBrandAsync(
        Guid brandId,
        CancellationToken cancellationToken = default)
    {
        if (brandId == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được rỗng.");
        }

        string relativeUri = $"api/v1/catalog/brands/{brandId:D}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var problem = JsonSerializer.Deserialize<RemoteProblemDetails>(content, JsonOptions);
                        if (problem is not null && !string.IsNullOrWhiteSpace(problem.Title) && string.Equals(problem.Title, "Brand.NotFound", StringComparison.OrdinalIgnoreCase))
                        {
                            return Error.Validation(problem.Title, problem.Detail ?? "Thương hiệu không tồn tại trong Catalog.");
                        }
                    }
                    catch (JsonException)
                    {
                        // Endpoint missing hoặc non-contract 404 response
                    }
                }

                return Error.Unavailable("CATALOG_SERVICE_ERROR", "CatalogService trả về mã lỗi HTTP 404.");
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "CatalogService trả về nội dung rỗng.");
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(content);
                }
                catch (JsonException ex)
                {
                    if (_logger is not null) LogJsonError(_logger, "get-brand", ex);
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService không đúng định dạng JSON.");
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService phải là JSON object.");
                    }

                    if (!root.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var parsedId) || parsedId == Guid.Empty)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử thiếu hoặc sai trường 'id' hợp lệ.");
                    }

                    if (parsedId != brandId)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "ID của thương hiệu phản hồi không khớp với yêu cầu.");
                    }

                    if (!root.TryGetProperty("code", out var codeProp) || codeProp.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(codeProp.GetString()))
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử thiếu hoặc sai trường 'code' hợp lệ.");
                    }

                    if (!root.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(nameProp.GetString()))
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử thiếu hoặc sai trường 'name' hợp lệ.");
                    }

                    if (!root.TryGetProperty("isActive", out var isActiveProp) ||
                        (isActiveProp.ValueKind != JsonValueKind.True && isActiveProp.ValueKind != JsonValueKind.False))
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Trường 'isActive' của thương hiệu bị thiếu hoặc không phải boolean.");
                    }

                    bool isActive = isActiveProp.GetBoolean();

                    return Result.Success(new CatalogBrandResponse(
                        parsedId,
                        codeProp.GetString()!,
                        nameProp.GetString()!,
                        isActive));
                }
            }

            return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger is not null) LogTimeoutResource(_logger, brandId.ToString("D"), "get-brand", ex);
            return Error.Unavailable("CATALOG_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ CatalogService.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger is not null) LogNetworkErrorResource(_logger, brandId.ToString("D"), "get-brand", ex);
            return Error.Unavailable("CATALOG_SERVICE_NETWORK_ERROR", "Không thể kết nối tới CatalogService.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> ValidateSpecificationAsync(
        Guid equipmentTypeId,
        int schemaVersion,
        string specValues,
        CancellationToken cancellationToken = default)
    {
        if (equipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        if (schemaVersion < 1)
        {
            return Error.Validation("INVALID_SCHEMA_VERSION", "Schema Version phải là số nguyên dương lớn hơn hoặc bằng 1.");
        }

        if (string.IsNullOrWhiteSpace(specValues))
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không được để trống.");
        }

        JsonDocument specDoc;
        try
        {
            specDoc = JsonDocument.Parse(specValues);
        }
        catch (JsonException)
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không đúng định dạng JSON.");
        }

        string relativeUri = $"api/v1/catalog/equipment-types/{equipmentTypeId:D}/specifications/validate";

        try
        {
            using (specDoc)
            {
                if (specDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Error.Validation("INVALID_SPEC_VALUES", "Spec values phải là JSON object.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                string? token = GetAccessToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Error.Unavailable("CATALOG_SERVICE_TOKEN_MISSING", "Không tìm thấy access token trong authentication ticket để gọi CatalogService.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Post, relativeUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var payload = new
                {
                    schemaVersion,
                    specValues = specDoc.RootElement
                };

                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "CatalogService trả về nội dung rỗng.");
                    }

                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(content);
                    }
                    catch (JsonException ex)
                    {
                        if (_logger is not null) LogJsonError(_logger, "validate-specification", ex);
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService không đúng định dạng JSON.");
                    }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService phải là JSON object.");
                        }

                        if (!root.TryGetProperty("valid", out var validProp) ||
                            (validProp.ValueKind != JsonValueKind.True && validProp.ValueKind != JsonValueKind.False))
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Trường 'valid' không tồn tại hoặc không phải boolean.");
                        }

                        if (!root.TryGetProperty("errors", out var errorsProp) || errorsProp.ValueKind != JsonValueKind.Array)
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Trường 'errors' không tồn tại hoặc không phải mảng.");
                        }

                        bool isValid = validProp.GetBoolean();
                        int errorCount = errorsProp.GetArrayLength();

                        if (isValid)
                        {
                            if (errorCount != 0)
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi mâu thuẫn: valid là true nhưng errors không rỗng.");
                            }
                            return Result.Success();
                        }

                        if (errorCount == 0)
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi mâu thuẫn: valid là false nhưng errors rỗng.");
                        }

                        var validationErrors = new List<ValidationError>(errorCount);
                        foreach (var item in errorsProp.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử trong 'errors' phải là JSON object.");
                            }

                            if (!item.TryGetProperty("field", out var fieldProp) || fieldProp.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(fieldProp.GetString()))
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử trong 'errors' thiếu trường 'field' hợp lệ.");
                            }

                            if (!item.TryGetProperty("code", out var codeProp) || codeProp.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(codeProp.GetString()))
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử trong 'errors' thiếu trường 'code' hợp lệ.");
                            }

                            if (!item.TryGetProperty("message", out var messageProp) || messageProp.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(messageProp.GetString()))
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử trong 'errors' thiếu trường 'message' hợp lệ.");
                            }

                            string code = codeProp.GetString()!;
                            if (!AllowedFieldValidationCodes.Contains(code))
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Mã lỗi field '{code}' không thuộc contract Catalog specification validation.");
                            }

                            validationErrors.Add(new ValidationError(
                                fieldProp.GetString()!,
                                code,
                                messageProp.GetString()!));
                        }

                        return Result.Failure(Error.Validation(
                            "SPEC_VALIDATION_FAILED",
                            "Dữ liệu specification không hợp lệ theo schema version đã công khai.",
                            validationErrors));
                    }
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        try
                        {
                            var problem = JsonSerializer.Deserialize<RemoteProblemDetails>(content, JsonOptions);
                            if (problem is not null && !string.IsNullOrWhiteSpace(problem.Title))
                            {
                                if (string.Equals(problem.Title, "EquipmentType.SchemaNotFound", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(problem.Title, "EquipmentType.NotFound", StringComparison.OrdinalIgnoreCase))
                                {
                                    return Error.Validation(problem.Title, problem.Detail ?? problem.Title);
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // Endpoint missing hoặc non-contract 404 response
                        }
                    }

                    return Error.Unavailable("CATALOG_SERVICE_ERROR", "CatalogService trả về mã lỗi HTTP 404.");
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        try
                        {
                            var problem = JsonSerializer.Deserialize<RemoteProblemDetails>(content, JsonOptions);
                            if (problem is not null && !string.IsNullOrWhiteSpace(problem.Title))
                            {
                                if (string.Equals(problem.Title, "Catalog.InvalidRequest", StringComparison.OrdinalIgnoreCase))
                                {
                                    return Error.Validation(problem.Title, problem.Detail ?? problem.Title);
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // Non-contract 400 response
                        }
                    }

                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi lỗi 400 từ CatalogService không đúng định dạng hoặc không thuộc contract.");
                }

                return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger is not null) LogTimeoutResource(_logger, equipmentTypeId.ToString("D"), "validate-specification", ex);
            return Error.Unavailable("CATALOG_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ CatalogService.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger is not null) LogNetworkErrorResource(_logger, equipmentTypeId.ToString("D"), "validate-specification", ex);
            return Error.Unavailable("CATALOG_SERVICE_NETWORK_ERROR", "Không thể kết nối tới CatalogService.");
        }
    }
}
