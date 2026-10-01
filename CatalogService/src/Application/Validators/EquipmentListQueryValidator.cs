using Dprental.CatalogService.Application.DTOs;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Validators;

public sealed class EquipmentListQueryValidator(IOptions<CatalogDiscoveryOptions> options)
{
    private static readonly Dictionary<string, EquipmentSort> SortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["newest"] = EquipmentSort.Newest,
        ["price_asc"] = EquipmentSort.PriceAscending,
        ["price_desc"] = EquipmentSort.PriceDescending
    };

    /// <summary>Không truyền sort thì mặc định newest. Giá trị lạ phải đã bị Validate chặn trước đó.</summary>
    public static EquipmentSort ParseSort(string? sort) =>
        string.IsNullOrWhiteSpace(sort) ? EquipmentSort.Newest : SortMap[sort.Trim()];

    public IReadOnlyList<ValidationError> Validate(EquipmentListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        CatalogDiscoveryOptions settings = options.Value;
        List<ValidationError> errors = [];

        if (query.Keyword is not null && query.Keyword.Trim().Length > settings.MaxKeywordLength)
            errors.Add(new(nameof(query.Keyword), $"Từ khóa tối đa {settings.MaxKeywordLength} ký tự."));

        if (query.MinPrice is < 0)
            errors.Add(new(nameof(query.MinPrice), "minPrice không được âm."));
        if (query.MaxPrice is < 0)
            errors.Add(new(nameof(query.MaxPrice), "maxPrice không được âm."));
        if (query.MinPrice is not null && query.MaxPrice is not null && query.MinPrice > query.MaxPrice)
            errors.Add(new(nameof(query.MinPrice), "minPrice không được lớn hơn maxPrice."));

        if (!string.IsNullOrWhiteSpace(query.Sort) && !SortMap.ContainsKey(query.Sort.Trim()))
            errors.Add(new(nameof(query.Sort), "sort chỉ nhận: newest, price_asc, price_desc."));

        if (query.Page is < 1)
            errors.Add(new(nameof(query.Page), "page phải >= 1."));
        if (query.PageSize is < 1 || query.PageSize > settings.MaxPageSize)
            errors.Add(new(nameof(query.PageSize), $"pageSize phải từ 1 đến {settings.MaxPageSize}."));

        return errors;
    }
}