using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Clients;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Application.Caching;

namespace Dprental.InventoryService.Application.Clients;

public sealed partial class CatalogServiceClient
{
    private static readonly HashSet<string> AllowedBatchFieldDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "number",
        "text",
        "enum",
        "boolean"
    };

    /// <inheritdoc />
    public async Task<Result<CachedRead<CatalogBatchSchemasResponse>>> GetEquipmentTypeSchemasBatchAsync(
        Guid equipmentTypeId,
        IReadOnlyList<int> versions,
        CancellationToken cancellationToken = default)
    {
        if (equipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }
        string scope = "catalog";
        string queryType = "batch-schemas";
        var normalizedVersions = (versions ?? Array.Empty<int>()).Distinct().OrderBy(v => v).ToList();
        string inputHash = $"{equipmentTypeId:D}_{string.Join(",", normalizedVersions)}";

        var pipelineResult = await _cache.GetOrCreateAsync<CatalogBatchSchemasResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                string relativeUri = $"api/v1/catalog/equipment-types/{equipmentTypeId:D}/schemas/batch";
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, relativeUri);
                    string? token = GetAccessToken();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }

                    var payload = new { versions = versions ?? Array.Empty<int>() };
                    request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

                    using var response = await _httpClient.SendAsync(request, ct);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        var content = await response.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            try
                            {
                                var problem = JsonSerializer.Deserialize<RemoteProblemDetails>(content, JsonOptions);
                                if (problem is not null && !string.IsNullOrWhiteSpace(problem.Title))
                                {
                                    // Chỉ map 404 có mã contract: EquipmentType.NotFound hoặc EquipmentType.SchemaNotFound
                                    if (string.Equals(problem.Title, "EquipmentType.NotFound", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(problem.Title, "EquipmentType.SchemaNotFound", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return Error.Validation(problem.Title, problem.Detail ?? problem.Title);
                                    }

                                    // 404 ngoài contract (RouteNotFound, Gateway, v.v.)
                                    return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP 404 ngoài contract: '{problem.Title}'.");
                                }
                            }
                            catch (JsonException)
                            {
                                // Malformed 404
                            }
                        }

                        // Response rỗng, HTML hoặc không thuộc contract -> 503
                        return Error.Unavailable("CATALOG_SERVICE_ERROR", "CatalogService trả về mã lỗi HTTP 404 ngoài contract hoặc nội dung không hợp lệ.");
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
                                    // Chỉ map 400 có mã contract: Catalog.InvalidRequest
                                    if (string.Equals(problem.Title, "Catalog.InvalidRequest", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return Error.Validation(problem.Title, problem.Detail ?? problem.Title);
                                    }

                                    return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP 400 ngoài contract: '{problem.Title}'.");
                                }
                            }
                            catch (JsonException)
                            {
                                // Malformed 400
                            }
                        }

                        return Error.Unavailable("CATALOG_SERVICE_ERROR", "CatalogService trả về mã lỗi HTTP 400 ngoài contract hoặc nội dung không hợp lệ.");
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var content = await response.Content.ReadAsStringAsync(ct);
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
                            if (_logger is not null) LogJsonError(_logger, "get-schemas-batch", ex);
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService không đúng định dạng JSON.");
                        }

                        using (doc)
                        {
                            var parseResult = ParseBatchSchemasResponse(doc.RootElement, equipmentTypeId, versions);
                            if (parseResult.IsFailure)
                            {
                                return parseResult.Error;
                            }
                            var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.CatalogTtlSeconds > 0 ? _cacheOptions.CatalogTtlSeconds : 120);
                            return Result.Success(new CachedRead<CatalogBatchSchemasResponse>(parseResult.Value, ttl));
                        }
                    }

                    return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP {(int)response.StatusCode}.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    if (_logger is not null) LogTimeoutResource(_logger, equipmentTypeId.ToString("D"), "get-schemas-batch", ex);
                    return Error.Unavailable("CATALOG_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ CatalogService.");
                }
                catch (HttpRequestException ex)
                {
                    if (_logger is not null) LogNetworkErrorResource(_logger, equipmentTypeId.ToString("D"), "get-schemas-batch", ex);
                    return Error.Unavailable("CATALOG_SERVICE_NETWORK_ERROR", "Không thể kết nối tới CatalogService.");
                }
            },
            cancellationToken);

        if (pipelineResult.IsFailure)
        {
            return pipelineResult.Error;
        }

        var deadline = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.CatalogTtlSeconds > 0 ? _cacheOptions.CatalogTtlSeconds : 120);
        return Result.Success(new CachedRead<CatalogBatchSchemasResponse>(pipelineResult.Value, deadline));
    }

    private static Result<CatalogBatchSchemasResponse> ParseBatchSchemasResponse(
        JsonElement root,
        Guid equipmentTypeId,
        IReadOnlyList<int>? requestedVersions)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService phải là JSON object.");
        }

        if (!root.TryGetProperty("equipmentTypeId", out var idProp) ||
            idProp.ValueKind != JsonValueKind.String ||
            !idProp.TryGetGuid(out var parsedId) ||
            parsedId != equipmentTypeId ||
            parsedId == Guid.Empty)
        {
            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử thiếu hoặc sai trường 'equipmentTypeId'.");
        }

        if (!root.TryGetProperty("currentSchemaVersion", out var currProp) ||
            currProp.ValueKind != JsonValueKind.Number ||
            !currProp.TryGetInt32(out var currentSchemaVersion) ||
            currentSchemaVersion <= 0)
        {
            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử thiếu hoặc sai trường 'currentSchemaVersion'.");
        }

        if (!root.TryGetProperty("schemas", out var schemasProp) || schemasProp.ValueKind != JsonValueKind.Array)
        {
            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử thiếu hoặc sai trường 'schemas'.");
        }

        var seenVersions = new HashSet<int>();
        var schemaList = new List<CatalogBatchSchemaItemResponse>();

        foreach (var sElem in schemasProp.EnumerateArray())
        {
            if (sElem.ValueKind != JsonValueKind.Object ||
                !sElem.TryGetProperty("version", out var vProp) ||
                vProp.ValueKind != JsonValueKind.Number ||
                !vProp.TryGetInt32(out var version) ||
                version <= 0 ||
                !sElem.TryGetProperty("fields", out var fProp) ||
                fProp.ValueKind != JsonValueKind.Array)
            {
                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử schema trong 'schemas' không hợp lệ.");
            }

            if (!seenVersions.Add(version))
            {
                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Trùng lặp phiên bản schema: {version}.");
            }

            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var fieldList = new List<CatalogBatchFieldResponse>();

            foreach (var fElem in fProp.EnumerateArray())
            {
                if (fElem.ValueKind != JsonValueKind.Object)
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Field trong schema không phải là JSON object.");
                }

                if (!fElem.TryGetProperty("key", out var keyProp) ||
                    keyProp.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(keyProp.GetString()))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Field thiếu hoặc sai thuộc tính 'key'.");
                }

                string key = keyProp.GetString()!;
                if (!seenKeys.Add(key))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Trùng lặp trường 'key': '{key}' trong version {version}.");
                }

                if (!fElem.TryGetProperty("dataType", out var dtProp) ||
                    dtProp.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(dtProp.GetString()))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' thiếu thuộc tính 'dataType' hợp lệ.");
                }

                string rawDataType = dtProp.GetString()!;
                if (!AllowedBatchFieldDataTypes.Contains(rawDataType))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có dataType '{rawDataType}' ngoài whitelist.");
                }

                // filterable bắt buộc phải có và phải là boolean
                if (!fElem.TryGetProperty("filterable", out var filtProp) ||
                    (filtProp.ValueKind != JsonValueKind.True && filtProp.ValueKind != JsonValueKind.False))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' thiếu hoặc sai kiểu thuộc tính 'filterable' (phải là boolean).");
                }
                bool filterable = filtProp.GetBoolean();

                // required: nếu có thì phải là boolean hoặc null
                bool required = false;
                if (fElem.TryGetProperty("required", out var reqProp))
                {
                    if (reqProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        required = reqProp.GetBoolean();
                    }
                    else if (reqProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'required' không phải boolean.");
                    }
                }

                // primary: nếu có thì phải là boolean hoặc null
                bool primary = false;
                if (fElem.TryGetProperty("primary", out var primProp))
                {
                    if (primProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        primary = primProp.GetBoolean();
                    }
                    else if (primProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'primary' không phải boolean.");
                    }
                }

                // displayOrder: nếu có thì phải là number
                int displayOrder = 0;
                if (fElem.TryGetProperty("displayOrder", out var doProp))
                {
                    if (doProp.ValueKind == JsonValueKind.Number)
                    {
                        if (!doProp.TryGetInt32(out displayOrder))
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'displayOrder' vượt quá kiểu int.");
                        }
                    }
                    else if (doProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'displayOrder' không phải số.");
                    }
                }

                // label: nếu có thì phải là string hoặc null
                string label = string.Empty;
                if (fElem.TryGetProperty("label", out var lProp))
                {
                    if (lProp.ValueKind == JsonValueKind.String)
                    {
                        label = lProp.GetString()!;
                    }
                    else if (lProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'label' không phải chuỗi.");
                    }
                }

                // min: nullable bounds chấp nhận omitted/null; kiểu sai hoặc không biểu diễn được trả 503
                decimal? min = null;
                if (fElem.TryGetProperty("min", out var minProp))
                {
                    if (minProp.ValueKind == JsonValueKind.Number)
                    {
                        if (!minProp.TryGetDecimal(out var minVal))
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'min' không biểu diễn được dưới dạng decimal.");
                        }
                        min = minVal;
                    }
                    else if (minProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'min' sai kiểu dữ liệu.");
                    }
                }

                // max: nullable bounds chấp nhận omitted/null; kiểu sai hoặc không biểu diễn được trả 503
                decimal? max = null;
                if (fElem.TryGetProperty("max", out var maxProp))
                {
                    if (maxProp.ValueKind == JsonValueKind.Number)
                    {
                        if (!maxProp.TryGetDecimal(out var maxVal))
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'max' không biểu diễn được dưới dạng decimal.");
                        }
                        max = maxVal;
                    }
                    else if (maxProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'max' sai kiểu dữ liệu.");
                    }
                }

                // unit: nullable string
                string? unit = null;
                if (fElem.TryGetProperty("unit", out var unitProp))
                {
                    if (unitProp.ValueKind == JsonValueKind.String)
                    {
                        unit = unitProp.GetString();
                    }
                    else if (unitProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'unit' sai kiểu dữ liệu (phải là string hoặc null).");
                    }
                }

                // description: nullable string
                string? description = null;
                if (fElem.TryGetProperty("description", out var descProp))
                {
                    if (descProp.ValueKind == JsonValueKind.String)
                    {
                        description = descProp.GetString();
                    }
                    else if (descProp.ValueKind != JsonValueKind.Null)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có 'description' sai kiểu dữ liệu (phải là string hoặc null).");
                    }
                }

                // options: kiểm tra enum options và tính duy nhất của option value
                List<CatalogBatchOptionResponse>? options = null;
                bool isEnum = string.Equals(rawDataType, "enum", StringComparison.OrdinalIgnoreCase);

                if (fElem.TryGetProperty("options", out var optsProp))
                {
                    if (optsProp.ValueKind == JsonValueKind.Array)
                    {
                        options = new List<CatalogBatchOptionResponse>();
                        var seenOptionValues = new HashSet<string>(StringComparer.Ordinal);

                        foreach (var optElem in optsProp.EnumerateArray())
                        {
                            if (optElem.ValueKind != JsonValueKind.Object)
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có phần tử option không phải object.");
                            }

                            if (!optElem.TryGetProperty("value", out var optValProp) ||
                                optValProp.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(optValProp.GetString()))
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có option thiếu 'value' hợp lệ.");
                            }

                            if (!optElem.TryGetProperty("label", out var optLblProp) ||
                                optLblProp.ValueKind != JsonValueKind.String)
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có option thiếu 'label' hợp lệ.");
                            }

                            string optValue = optValProp.GetString()!;
                            if (!seenOptionValues.Add(optValue))
                            {
                                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có trùng lặp option value: '{optValue}'.");
                            }

                            options.Add(new CatalogBatchOptionResponse(optValue, optLblProp.GetString()!));
                        }

                        if (isEnum && options.Count == 0)
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' kiểu enum nhưng danh sách options rỗng.");
                        }
                    }
                    else if (optsProp.ValueKind == JsonValueKind.Null)
                    {
                        if (isEnum)
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' kiểu enum không được phép có options là null.");
                        }
                    }
                    else
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' có options sai kiểu dữ liệu.");
                    }
                }
                else
                {
                    if (isEnum)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Field '{key}' kiểu enum bắt buộc phải có thuộc tính 'options'.");
                    }
                }

                fieldList.Add(new CatalogBatchFieldResponse(
                    key,
                    label,
                    rawDataType,
                    required,
                    filterable,
                    primary,
                    displayOrder,
                    min,
                    max,
                    options,
                    unit,
                    description));
            }

            schemaList.Add(new CatalogBatchSchemaItemResponse(version, fieldList));
        }

        if (!schemaList.Any(s => s.Version == currentSchemaVersion))
        {
            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService thiếu current schema version.");
        }

        if (requestedVersions != null)
        {
            foreach (int reqV in requestedVersions)
            {
                if (!schemaList.Any(s => s.Version == reqV))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", $"Phản hồi từ CatalogService thiếu version {reqV} được yêu cầu.");
                }
            }
        }

        return Result.Success(new CatalogBatchSchemasResponse(equipmentTypeId, currentSchemaVersion, schemaList));
    }
}
