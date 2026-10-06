using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

/// <summary>Yêu cầu tìm kiếm và phân trang loại thiết bị cho Admin, ở mọi trạng thái.</summary>
public sealed record GetAdminEquipmentTypesRequest
{
    public string? Keyword { get; init; }
    public Guid? CategoryId { get; init; }

    /// <summary>
    /// Lọc theo trạng thái: Draft, Published, Disabled. Lặp lại tham số để lấy nhiều trạng thái
    /// (vd: <c>status=Published&amp;status=Disabled</c>); bỏ trống để lấy tất cả.
    /// </summary>
    public IReadOnlyList<string>? Status { get; init; }

    public string? Sort { get; init; }
    public int Page { get; init; } = PageRequest.DefaultPage;
    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}
