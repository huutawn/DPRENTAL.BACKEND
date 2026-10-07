using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface IEquipmentTypeSchemaRepository
{
    // Luồng ghi (có tracking)
    Task<EquipmentType?> GetWithSchemaAsync(Guid id, CancellationToken cancellationToken = default);

    Task<EquipmentType?> GetBySchemaIdAsync(Guid specSchemaId, CancellationToken cancellationToken = default);

    Task<EquipmentType?> GetByVersionIdAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <returns>false nếu xung đột đồng thời (xmin) hoặc vi phạm unique.</returns>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default);

    // Luồng đọc (không tracking)
    Task<EquipmentType?> GetWithSchemaReadOnlyAsync(Guid id, CancellationToken cancellationToken = default);

    // Rule canonical
    Task<IReadOnlyList<Category>> GetCategoriesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<bool> ExistsCanonicalDuplicateAsync(
        string normalizedName,
        IReadOnlyCollection<Guid> categoryIds,
        Guid? excludeId,
        CancellationToken cancellationToken = default);
}