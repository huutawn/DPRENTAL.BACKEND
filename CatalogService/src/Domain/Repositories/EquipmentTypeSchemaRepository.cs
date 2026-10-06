using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.CatalogService.Domain.Repositories;

public class EquipmentTypeSchemaRepository(CatalogServiceDbContext dbContext) : IEquipmentTypeSchemaRepository
{
    public Task<EquipmentType?> GetWithSchemaAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EquipmentTypes.Include(e => e.SpecSchemas)
            .AsSplitQuery().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<EquipmentType?> GetBySchemaIdAsync(Guid specSchemaId, CancellationToken cancellationToken = default) =>
        GetWithSchemaAsync(specSchemaId, cancellationToken);

    public Task<EquipmentType?> GetByVersionIdAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        dbContext.EquipmentTypes.Include(e => e.SpecSchemas)
            .AsSplitQuery().FirstOrDefaultAsync(e => e.SpecSchemas.Any(s => s.Id == versionId), cancellationToken);

    public Task<EquipmentType?> GetWithSchemaReadOnlyAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EquipmentTypes.Include(e => e.SpecSchemas).AsNoTracking()
            .AsSplitQuery().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        Guid[] array = ids.ToArray();
        return await dbContext.Categories
            .AsNoTracking()
            .Where(category => array.Contains(category.Id))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> ExistsCanonicalDuplicateAsync(
        string normalizedName,
        IReadOnlyCollection<Guid> categoryIds,
        Guid? excludeId,
        CancellationToken cancellationToken = default)
    {
        Guid[] ids = categoryIds.ToArray();
        return dbContext.EquipmentTypes
            .AsNoTracking()
            .AnyAsync(
                e => e.NormalizedName == normalizedName
                     && (excludeId == null || e.Id != excludeId.Value)
                     && e.Categories.Any(category => ids.Contains(category.Id)),
                cancellationToken);
    }

}
