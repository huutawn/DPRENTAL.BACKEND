using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Domain.DTOs.Equipments;

/// <summary>Yêu cầu tìm kiếm và phân trang thiết bị trong catalog công khai.</summary>
public sealed record GetEquipmentsRequest
{
    public string? Keyword { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? RegionId { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}
