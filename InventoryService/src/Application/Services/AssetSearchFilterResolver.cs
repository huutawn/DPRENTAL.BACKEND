using System.Globalization;
using System.Text.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Kết quả phân giải bộ lọc động JSONB bao gồm các điều kiện lọc, các schema version tương thích và deadline của Catalog schema cache.
/// </summary>
public sealed record ResolvedAssetFilters(
    IReadOnlyList<AssetDynamicFilterCriterion>? Criteria,
    IReadOnlyList<int>? CompatibleSchemaVersions,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record RawDynamicFilter(
    string Field,
    string Operator,
    JsonElement Value);

/// <summary>
/// Thành phần phân giải, kiểm tra tính hợp lệ và xác định tính tương thích lịch sử của bộ lọc động JSONB theo Catalog schema.
/// </summary>
public sealed class AssetSearchFilterResolver
{
    private readonly ICatalogServiceClient _catalogClient;

    public AssetSearchFilterResolver(ICatalogServiceClient catalogClient)
    {
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
    }

    /// <summary>
    /// Phân giải chuỗi JSON filters thành danh sách tiêu chí lọc động và các version schema tương thích.
    /// Nếu filters rỗng, không gọi DB hay Catalog.
    /// </summary>
    public async Task<Result<ResolvedAssetFilters>> ResolveAsync(
        string? rawFiltersJson,
        Guid? equipmentTypeId,
        Func<CancellationToken, Task<IReadOnlyList<int>>> getCandidateVersionsAsync,
        Func<string, bool>? isFieldForbidden = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getCandidateVersionsAsync);

        var structCheck = ValidateStructure(rawFiltersJson, equipmentTypeId, isFieldForbidden);
        if (structCheck.IsFailure)
        {
            return structCheck.Error;
        }

        var rawFilters = structCheck.Value;
        if (rawFilters == null || rawFilters.Count == 0)
        {
            return Result.Success(new ResolvedAssetFilters(null, null));
        }

        var distinctVersions = await getCandidateVersionsAsync(cancellationToken);

        var catalogResult = await _catalogClient.GetEquipmentTypeSchemasBatchAsync(
            equipmentTypeId!.Value,
            distinctVersions,
            cancellationToken);

        if (catalogResult.IsFailure)
        {
            return catalogResult.Error;
        }

        var catalogCachedRead = catalogResult.Value;
        var catalogResponse = catalogCachedRead.Value;
        var schemaDeadline = catalogCachedRead.ExpiresAtUtc;

        var currentSchema = catalogResponse.Schemas.FirstOrDefault(s => s.Version == catalogResponse.CurrentSchemaVersion);
        if (currentSchema is null)
        {
            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "CatalogService không trả về current schema.");
        }

        var parsedDynamicFilters = new List<AssetDynamicFilterCriterion>(rawFilters.Count);

        foreach (var raw in rawFilters)
        {
            var curField = currentSchema.Fields.FirstOrDefault(f => string.Equals(f.Key, raw.Field, StringComparison.Ordinal));
            if (curField is null)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Trường '{raw.Field}' không tồn tại trong current schema.");
            }

            if (!curField.Filterable)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Trường '{raw.Field}' không được đánh dấu filterable trong current schema.");
            }

            string dataType = curField.DataType.ToLowerInvariant();
            if (dataType == "number")
            {
                if (raw.Operator is not ("eq" or "gt" or "gte" or "lt" or "lte"))
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Toán tử '{raw.Operator}' không hợp lệ cho trường kiểu number '{raw.Field}'.");
                }

                if (raw.Value.ValueKind != JsonValueKind.Number)
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Giá trị lọc cho trường number '{raw.Field}' phải là kiểu số.");
                }

                if (!raw.Value.TryGetDecimal(out decimal numVal))
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Giá trị lọc '{raw.Value}' cho trường number '{raw.Field}' vượt quá giới hạn số hợp lệ.");
                }

                parsedDynamicFilters.Add(new AssetDynamicFilterCriterion(raw.Field, raw.Operator, numVal, "number"));
            }
            else if (dataType is "text" or "string")
            {
                if (raw.Operator is not "eq")
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Toán tử '{raw.Operator}' không hợp lệ cho trường kiểu text '{raw.Field}'. Chỉ hỗ trợ 'eq'.");
                }

                if (raw.Value.ValueKind != JsonValueKind.String)
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Giá trị lọc cho trường text '{raw.Field}' phải là chuỗi.");
                }

                string strVal = raw.Value.GetString()!;
                parsedDynamicFilters.Add(new AssetDynamicFilterCriterion(raw.Field, raw.Operator, strVal, "text"));
            }
            else if (dataType == "boolean")
            {
                if (raw.Operator is not "eq")
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Toán tử '{raw.Operator}' không hợp lệ cho trường kiểu boolean '{raw.Field}'. Chỉ hỗ trợ 'eq'.");
                }

                if (raw.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Giá trị lọc cho trường boolean '{raw.Field}' phải là boolean.");
                }

                bool boolVal = raw.Value.GetBoolean();
                parsedDynamicFilters.Add(new AssetDynamicFilterCriterion(raw.Field, raw.Operator, boolVal, "boolean"));
            }
            else if (dataType == "enum")
            {
                if (raw.Operator is not "eq")
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Toán tử '{raw.Operator}' không hợp lệ cho trường kiểu enum '{raw.Field}'. Chỉ hỗ trợ 'eq'.");
                }

                if (raw.Value.ValueKind != JsonValueKind.String)
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Giá trị lọc cho trường enum '{raw.Field}' phải là chuỗi.");
                }

                string enumVal = raw.Value.GetString()!;
                bool isValidOption = curField.Options != null && curField.Options.Any(o => string.Equals(o.Value, enumVal, StringComparison.Ordinal));
                if (!isValidOption)
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Giá trị '{enumVal}' không phải là option hợp lệ của enum '{raw.Field}'.");
                }

                parsedDynamicFilters.Add(new AssetDynamicFilterCriterion(raw.Field, raw.Operator, enumVal, "enum"));
            }
            else
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Kiểu dữ liệu '{curField.DataType}' không hỗ trợ filter.");
            }
        }

        var compatibleVersions = new List<int>();

        foreach (var schemaItem in catalogResponse.Schemas)
        {
            bool isCompatible = true;
            foreach (var filter in parsedDynamicFilters)
            {
                var curField = currentSchema.Fields.First(f => string.Equals(f.Key, filter.Field, StringComparison.Ordinal));
                var histField = schemaItem.Fields.FirstOrDefault(f => string.Equals(f.Key, filter.Field, StringComparison.Ordinal));

                if (histField is null || !histField.Filterable)
                {
                    isCompatible = false;
                    break;
                }

                if (!string.Equals(histField.DataType, curField.DataType, StringComparison.OrdinalIgnoreCase))
                {
                    isCompatible = false;
                    break;
                }

                if (!string.Equals(histField.Unit, curField.Unit, StringComparison.Ordinal))
                {
                    isCompatible = false;
                    break;
                }

                if (string.Equals(filter.DataType, "enum", StringComparison.OrdinalIgnoreCase))
                {
                    string filterVal = (string)filter.Value;
                    bool hasOption = histField.Options != null && histField.Options.Any(o => string.Equals(o.Value, filterVal, StringComparison.Ordinal));
                    if (!hasOption)
                    {
                        isCompatible = false;
                        break;
                    }
                }
            }

            if (isCompatible)
            {
                compatibleVersions.Add(schemaItem.Version);
            }
        }

        return Result.Success(new ResolvedAssetFilters(parsedDynamicFilters, compatibleVersions, schemaDeadline));
    }

    /// <summary>
    /// Thẩm tra cấu trúc cú pháp của raw JSON filters độc lập trước khi thực hiện tra cứu cache.
    /// </summary>
    public static Result<IReadOnlyList<RawDynamicFilter>?> ValidateStructure(
        string? rawFiltersJson,
        Guid? equipmentTypeId,
        Func<string, bool>? isFieldForbidden = null)
    {
        if (string.IsNullOrWhiteSpace(rawFiltersJson))
        {
            return Result.Success<IReadOnlyList<RawDynamicFilter>?>(null);
        }

        string trimmedFilters = rawFiltersJson.Trim();
        if (string.Equals(trimmedFilters, "null", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "Filters không được là chuỗi 'null'.");
        }

        JsonDocument filterDoc;
        try
        {
            filterDoc = JsonDocument.Parse(trimmedFilters);
        }
        catch (JsonException)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "JSON của filters không đúng định dạng.");
        }

        using (filterDoc)
        {
            if (filterDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Filters phải là một JSON array.");
            }

            int filterCount = filterDoc.RootElement.GetArrayLength();
            if (filterCount > 20)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Dynamic filters tối đa 20 điều kiện.");
            }

            if (filterCount == 0)
            {
                return Result.Success<IReadOnlyList<RawDynamicFilter>?>(null);
            }

            if (!equipmentTypeId.HasValue || equipmentTypeId.Value == Guid.Empty)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Dynamic filters bắt buộc phải có equipmentTypeId.");
            }

            var rawFilters = new List<RawDynamicFilter>(filterCount);

            foreach (var elem in filterDoc.RootElement.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Object)
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", "Mỗi phần tử filter phải là một JSON object.");
                }

                int propCount = 0;
                string? fieldName = null;
                string? opName = null;
                JsonElement? valElem = null;

                foreach (var prop in elem.EnumerateObject())
                {
                    propCount++;
                    if (string.Equals(prop.Name, "field", StringComparison.Ordinal))
                    {
                        if (prop.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prop.Value.GetString()))
                        {
                            return Error.Validation("INVALID_QUERY_PARAMETER", "Thuộc tính 'field' phải là chuỗi không rỗng.");
                        }
                        fieldName = prop.Value.GetString();
                    }
                    else if (string.Equals(prop.Name, "operator", StringComparison.Ordinal))
                    {
                        if (prop.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prop.Value.GetString()))
                        {
                            return Error.Validation("INVALID_QUERY_PARAMETER", "Thuộc tính 'operator' phải là chuỗi không rỗng.");
                        }
                        opName = prop.Value.GetString();
                    }
                    else if (string.Equals(prop.Name, "value", StringComparison.Ordinal))
                    {
                        if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Array or JsonValueKind.Object)
                        {
                            return Error.Validation("INVALID_QUERY_PARAMETER", "Thuộc tính 'value' không hợp lệ.");
                        }
                        valElem = prop.Value.Clone();
                    }
                    else
                    {
                        return Error.Validation("INVALID_QUERY_PARAMETER", $"Thuộc tính không được hỗ trợ trong filter: '{prop.Name}'. Chỉ chấp nhận field, operator, value.");
                    }
                }

                if (propCount != 3 || fieldName is null || opName is null || valElem is null)
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", "Mỗi filter object phải có đủ 3 thuộc tính: field, operator, value.");
                }

                if (isFieldForbidden != null && isFieldForbidden(fieldName))
                {
                    return Error.Validation("INVALID_QUERY_PARAMETER", $"Thuộc tính '{fieldName}' không được phép sử dụng trong bộ lọc tìm kiếm.");
                }

                rawFilters.Add(new RawDynamicFilter(fieldName, opName, valElem.Value));
            }

            return Result.Success<IReadOnlyList<RawDynamicFilter>?>(rawFilters);
        }
    }
}
