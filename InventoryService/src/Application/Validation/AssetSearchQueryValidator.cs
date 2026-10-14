using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Validation;

/// <summary>
/// Kết quả chuẩn hóa và hợp lệ của các tham số tìm kiếm tài sản dùng chung.
/// </summary>
public sealed record ValidatedAssetSearchQuery(
    int Page,
    int PageSize,
    int Skip,
    int Take,
    string? SearchKeyword,
    Guid? EquipmentTypeId,
    Guid? BrandId,
    int? ManufactureYearFrom,
    int? ManufactureYearTo,
    AssetSortOption Sort,
    Guid? CategoryId = null);

/// <summary>
/// Validator dùng chung cho các tham số tìm kiếm, phân trang và sắp xếp danh sách tài sản.
/// </summary>
public sealed class AssetSearchQueryValidator
{
    private readonly TimeProvider _timeProvider;

    public AssetSearchQueryValidator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Kiểm tra và chuẩn hóa các tham số tìm kiếm cơ bản.
    /// </summary>
    public Result<ValidatedAssetSearchQuery> Validate(
        int page,
        int pageSize,
        string? q,
        Guid? equipmentTypeId,
        Guid? brandId,
        int? manufactureYearFrom,
        int? manufactureYearTo,
        string? sort)
    {
        if (page < 1)
        {
            return Error.Validation("INVALID_PAGE", "Số trang phải lớn hơn hoặc bằng 1.");
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return Error.Validation("INVALID_PAGE_SIZE", "Kích thước trang phải từ 1 đến 100.");
        }

        long offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Error.Validation("INVALID_PAGE", "Chỉ số trang vượt quá giới hạn hệ thống.");
        }

        int skip = (int)offset;
        int take = pageSize;

        var keywordResult = ValidateSearchKeyword(q);
        if (keywordResult.IsFailure)
        {
            return keywordResult.Error;
        }
        string? searchKeyword = keywordResult.Value;

        if (equipmentTypeId.HasValue && equipmentTypeId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "EquipmentTypeId không được là Guid rỗng.");
        }

        if (brandId.HasValue && brandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "BrandId không được là Guid rỗng.");
        }

        int currentYear = BusinessDateTime.ToVietnamDate(_timeProvider.GetUtcNow()).Year;
        if (manufactureYearFrom.HasValue)
        {
            if (manufactureYearFrom.Value < 1900 || manufactureYearFrom.Value > currentYear)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Năm sản xuất từ (From) phải nằm trong khoảng từ 1900 đến {currentYear}.");
            }
        }

        if (manufactureYearTo.HasValue)
        {
            if (manufactureYearTo.Value < 1900 || manufactureYearTo.Value > currentYear)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", $"Năm sản xuất đến (To) phải nằm trong khoảng từ 1900 đến {currentYear}.");
            }
        }

        if (manufactureYearFrom.HasValue && manufactureYearTo.HasValue)
        {
            if (manufactureYearFrom.Value > manufactureYearTo.Value)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Năm sản xuất từ (From) không được lớn hơn năm sản xuất đến (To).");
            }
        }

        AssetSortOption sortOption = !string.IsNullOrWhiteSpace(searchKeyword)
            ? AssetSortOption.Relevance
            : AssetSortOption.CreatedDesc;

        if (!string.IsNullOrWhiteSpace(sort))
        {
            string sortStr = sort.Trim().ToLowerInvariant();
            sortOption = sortStr switch
            {
                "created_desc" => AssetSortOption.CreatedDesc,
                "created_asc" => AssetSortOption.CreatedAsc,
                "model_desc" => AssetSortOption.ModelDesc,
                "model_asc" => AssetSortOption.ModelAsc,
                "manufacture_year_desc" => AssetSortOption.ManufactureYearDesc,
                "manufacture_year_asc" => AssetSortOption.ManufactureYearAsc,
                "relevance" => AssetSortOption.Relevance,
                _ => (AssetSortOption)(-1)
            };

            if ((int)sortOption == -1)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Tùy chọn sắp xếp (sort) không hợp lệ.");
            }
        }

        return Result.Success(new ValidatedAssetSearchQuery(
            page,
            pageSize,
            skip,
            take,
            searchKeyword,
            equipmentTypeId,
            brandId,
            manufactureYearFrom,
            manufactureYearTo,
            sortOption));
    }

    /// <summary>
    /// Kiểm tra và chuẩn hóa các tham số tìm kiếm danh mục EquipmentListing công khai (không có manufactureYear).
    /// </summary>
    public static Result<ValidatedAssetSearchQuery> ValidateListingQuery(
        int page,
        int pageSize,
        string? q,
        Guid? equipmentTypeId,
        Guid? brandId,
        string? sort,
        Guid? categoryId = null)
    {
        if (page < 1)
        {
            return Error.Validation("INVALID_PAGE", "Số trang phải lớn hơn hoặc bằng 1.");
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return Error.Validation("INVALID_PAGE_SIZE", "Kích thước trang phải từ 1 đến 100.");
        }

        long offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Error.Validation("INVALID_PAGE", "Chỉ số trang vượt quá giới hạn hệ thống.");
        }

        int skip = (int)offset;
        int take = pageSize;

        var keywordResult = ValidateSearchKeyword(q);
        if (keywordResult.IsFailure)
        {
            return keywordResult.Error;
        }
        string? searchKeyword = keywordResult.Value;

        if (equipmentTypeId.HasValue && equipmentTypeId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "EquipmentTypeId không được là Guid rỗng.");
        }

        if (brandId.HasValue && brandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "BrandId không được là Guid rỗng.");
        }

        if (categoryId.HasValue && categoryId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "CategoryId không hợp lệ.");
        }

        AssetSortOption sortOption = !string.IsNullOrWhiteSpace(searchKeyword)
            ? AssetSortOption.Relevance
            : AssetSortOption.CreatedDesc;

        if (!string.IsNullOrWhiteSpace(sort))
        {
            string sortStr = sort.Trim().ToLowerInvariant();
            sortOption = sortStr switch
            {
                "created_desc" => AssetSortOption.CreatedDesc,
                "created_asc" => AssetSortOption.CreatedAsc,
                "model_desc" => AssetSortOption.ModelDesc,
                "model_asc" => AssetSortOption.ModelAsc,
                "relevance" => AssetSortOption.Relevance,
                _ => (AssetSortOption)(-1)
            };

            if ((int)sortOption == -1)
            {
                return Error.Validation("INVALID_QUERY_PARAMETER", "Tùy chọn sắp xếp (sort) không hợp lệ.");
            }
        }

        return Result.Success(new ValidatedAssetSearchQuery(
            page,
            pageSize,
            skip,
            take,
            searchKeyword,
            equipmentTypeId,
            brandId,
            null,
            null,
            sortOption,
            categoryId));
    }

    /// <summary>
    /// Xác thực và chuẩn hóa từ khóa tìm kiếm (q): trim, tối đa 200 ký tự; thiếu hoặc rỗng trả về null.
    /// </summary>
    public static Result<string?> ValidateSearchKeyword(string? q)
    {
        if (q is null)
        {
            return Result.Success<string?>(null);
        }

        string trimmed = q.Trim();
        if (trimmed.Length > 200)
        {
            return Error.Validation("INVALID_QUERY_PARAMETER", "Từ khóa tìm kiếm tối đa 200 ký tự.");
        }

        return Result.Success<string?>(trimmed.Length > 0 ? trimmed : null);
    }
}
