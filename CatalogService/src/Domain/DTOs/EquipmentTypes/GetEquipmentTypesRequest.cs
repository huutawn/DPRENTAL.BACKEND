using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

/// <summary>Yêu cầu tìm kiếm và phân trang loại thiết bị trong catalog công khai.</summary>
public sealed record GetEquipmentTypesRequest
{
    public string? Keyword { get; init; }
    public Guid? CategoryId { get; init; }
    public string? Sort { get; init; }
    public string? Scope { get; init; }
    public string? ListingStatus { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}
