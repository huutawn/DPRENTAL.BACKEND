using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface IEquipmentQueryRepository
{
    /// <summary>Luôn đọc Status = Published. Filter, sort ổn định, phân trang và projection đều ở database.</summary>
    Task<PagedResult<EquipmentListItemDto>> SearchPublishedAsync(
        GetEquipmentsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Trả null nếu không tồn tại hoặc không Published (Draft/Hidden không lộ ra khi đoán id).</summary>
    Task<EquipmentDetailDto?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
