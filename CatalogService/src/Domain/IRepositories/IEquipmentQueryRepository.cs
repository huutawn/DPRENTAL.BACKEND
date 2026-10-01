using Dprental.CatalogService.Domain.ValueObjects;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface IEquipmentQueryRepository
{
    /// <summary> Luôn đọc Stauts = published. Filter, sort ổn định, phân trang và projection đều ở database. </summary>
    Task<PagedResult<EquipmentListItem>> SearchPublishedAsync(
        EquipmentSearchCriteria criteria,
        CancellationToken cancellationToken = default
        );
    /// <summary>Trả null nếu không tồn tại hoặc không Published (Draft/Hidden không lộ ra khi đoán id).</summary>
    Task<EquipmentDetail?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
        );
}