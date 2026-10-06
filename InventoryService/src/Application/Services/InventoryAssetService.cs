using System.Text.Json;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Application service điều phối nghiệp vụ quản lý tài sản (InventoryAsset).
/// Phần triển khai các tác vụ đọc (Get, List) và các hàm kiểm tra dùng chung.
/// </summary>
public sealed partial class InventoryAssetService : IInventoryAssetService
{
    private readonly IInventoryAssetRepository _assetRepository;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly ICatalogServiceClient _catalogClient;
    private readonly IInventoryMediaService _mediaService;
    private readonly TimeProvider _timeProvider;

    public InventoryAssetService(
        IInventoryAssetRepository assetRepository,
        IInventoryWriteTransaction writeTransaction,
        ICatalogServiceClient catalogClient,
        IInventoryMediaService mediaService,
        TimeProvider? timeProvider = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Lấy chi tiết tài sản theo ID với phân quyền truy cập.
    /// </summary>
    public async Task<Result<InventoryAssetResponse>> GetAssetByIdAsync(
        Guid assetId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var callerCheck = CheckAuthenticatedHuman(caller);
        if (callerCheck.IsFailure)
        {
            return callerCheck.Error;
        }

        var asset = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanManageAsset(caller, asset.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền truy cập tài sản này.");
        }

        return Result.Success(InventoryAssetMapper.ToResponse(asset));
    }

    /// <summary>
    /// Lấy danh sách tài sản phân trang của người dùng hiện tại (JWT sub).
    /// </summary>
    public async Task<Result<PagedResponse<InventoryAssetResponse>>> ListAssetsAsync(
        ListAssetsRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var authResult = AuthorizeManager(caller);
        if (authResult.IsFailure)
        {
            return authResult.Error;
        }

        Guid ownerUserId = caller.UserId!.Value;

        if (request.Page < 1)
        {
            return Error.Validation("INVALID_PAGE", "Số trang phải lớn hơn hoặc bằng 1.");
        }

        if (request.PageSize < 1 || request.PageSize > 100)
        {
            return Error.Validation("INVALID_PAGE_SIZE", "Kích thước trang phải từ 1 đến 100.");
        }

        long offset = (long)(request.Page - 1) * request.PageSize;
        if (offset > int.MaxValue)
        {
            return Error.Validation("INVALID_PAGE", "Chỉ số trang vượt quá giới hạn hệ thống.");
        }

        int skip = (int)offset;
        int take = request.PageSize;

        string? searchKeyword = null;
        if (request.Q is not null)
        {
            string trimmed = request.Q.Trim();
            if (trimmed.Length > 200)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Từ khóa tìm kiếm tối đa 200 ký tự.");
            }
            if (trimmed.Length > 0)
            {
                searchKeyword = trimmed;
            }
        }

        if (request.EquipmentTypeId.HasValue && request.EquipmentTypeId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "EquipmentTypeId không được là Guid rỗng.");
        }

        if (request.BrandId.HasValue && request.BrandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "BrandId không được là Guid rỗng.");
        }

        int currentYear = BusinessDateTime.ToVietnamDate(_timeProvider.GetUtcNow()).Year;
        if (request.ManufactureYearFrom.HasValue)
        {
            if (request.ManufactureYearFrom.Value < 1900 || request.ManufactureYearFrom.Value > currentYear)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Năm sản xuất từ (From) phải nằm trong khoảng từ 1900 đến {currentYear}.");
            }
        }

        if (request.ManufactureYearTo.HasValue)
        {
            if (request.ManufactureYearTo.Value < 1900 || request.ManufactureYearTo.Value > currentYear)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Năm sản xuất đến (To) phải nằm trong khoảng từ 1900 đến {currentYear}.");
            }
        }

        if (request.ManufactureYearFrom.HasValue && request.ManufactureYearTo.HasValue)
        {
            if (request.ManufactureYearFrom.Value > request.ManufactureYearTo.Value)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Năm sản xuất từ (From) không được lớn hơn năm sản xuất đến (To).");
            }
        }

        AssetSortOption sortOption = AssetSortOption.CreatedDesc;
        if (!string.IsNullOrWhiteSpace(request.Sort))
        {
            string sortStr = request.Sort.Trim().ToLowerInvariant();
            sortOption = sortStr switch
            {
                "created_desc" => AssetSortOption.CreatedDesc,
                "created_asc" => AssetSortOption.CreatedAsc,
                "model_desc" => AssetSortOption.ModelDesc,
                "model_asc" => AssetSortOption.ModelAsc,
                "manufacture_year_desc" => AssetSortOption.ManufactureYearDesc,
                "manufacture_year_asc" => AssetSortOption.ManufactureYearAsc,
                _ => (AssetSortOption)(-1)
            };

            if ((int)sortOption == -1)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Tùy chọn sắp xếp (sort) không hợp lệ.");
            }
        }

        List<RawDynamicFilter>? rawFilters = null;
        if (!string.IsNullOrWhiteSpace(request.Filters))
        {
            string trimmedFilters = request.Filters.Trim();
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

                if (filterCount > 0)
                {
                    if (!request.EquipmentTypeId.HasValue || request.EquipmentTypeId.Value == Guid.Empty)
                    {
                        return Error.Validation("INVALID_QUERY_PARAMETER", "Dynamic filters bắt buộc phải có equipmentTypeId.");
                    }

                    rawFilters = new List<RawDynamicFilter>(filterCount);

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

                        rawFilters.Add(new RawDynamicFilter(fieldName, opName, valElem.Value));
                    }
                }
            }
        }

        List<AssetDynamicFilterCriterion>? parsedDynamicFilters = null;
        List<int>? compatibleVersions = null;

        if (rawFilters != null && rawFilters.Count > 0)
        {
            var metadataCriteria = new AssetSearchCriteria(
                OwnerUserId: ownerUserId,
                SearchKeyword: searchKeyword,
                EquipmentTypeId: request.EquipmentTypeId,
                BrandId: request.BrandId,
                AcceptingBookings: request.AcceptingBookings,
                ManufactureYearFrom: request.ManufactureYearFrom,
                ManufactureYearTo: request.ManufactureYearTo);

            var distinctVersions = await _assetRepository.GetDistinctSchemaVersionsAsync(metadataCriteria, cancellationToken);

            var catalogResult = await _catalogClient.GetEquipmentTypeSchemasBatchAsync(
                request.EquipmentTypeId!.Value,
                distinctVersions,
                cancellationToken);

            if (catalogResult.IsFailure)
            {
                return catalogResult.Error;
            }

            var catalogResponse = catalogResult.Value;
            var currentSchema = catalogResponse.Schemas.FirstOrDefault(s => s.Version == catalogResponse.CurrentSchemaVersion);
            if (currentSchema is null)
            {
                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "CatalogService không trả về current schema.");
            }

            parsedDynamicFilters = new List<AssetDynamicFilterCriterion>();

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

            compatibleVersions = new List<int>();

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
        }

        var searchCriteria = new AssetSearchCriteria(
            OwnerUserId: ownerUserId,
            SearchKeyword: searchKeyword,
            EquipmentTypeId: request.EquipmentTypeId,
            BrandId: request.BrandId,
            AcceptingBookings: request.AcceptingBookings,
            ManufactureYearFrom: request.ManufactureYearFrom,
            ManufactureYearTo: request.ManufactureYearTo,
            Sort: sortOption,
            CompatibleSchemaVersions: parsedDynamicFilters != null ? compatibleVersions : null,
            DynamicFilters: parsedDynamicFilters,
            Skip: skip,
            Take: take);

        var items = await _assetRepository.SearchAsync(searchCriteria, cancellationToken);
        var totalCount = await _assetRepository.CountAsync(searchCriteria, cancellationToken);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / take);

        var responseList = items.Select(InventoryAssetMapper.ToResponse).ToList();
        return Result.Success(new PagedResponse<InventoryAssetResponse>(responseList, request.Page, request.PageSize, totalCount, totalPages));
    }

    private sealed record RawDynamicFilter(
        string Field,
        string Operator,
        JsonElement Value);


    private static Result CheckAuthenticatedHuman(IInventoryCallerContext caller)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsHuman || caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân hợp lệ mới được thực hiện thao tác này.");
        }

        return Result.Success();
    }

    private static Result AuthorizeManager(IInventoryCallerContext caller)
    {
        var check = CheckAuthenticatedHuman(caller);
        if (check.IsFailure)
        {
            return check;
        }

        if (!caller.IsAdmin && !caller.Permissions.Contains(InventoryAccessEvaluator.PermissionAssetManage))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền quản lý tài sản trong kho.");
        }

        return Result.Success();
    }
}
