using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class EquipmentTypeRepository(CatalogServiceDbContext dbContext) : IEquipmentTypeRepository
{
    /// <summary>Tên index unique trong DB, dùng để nhận diện vi phạm khi hai request chạy cùng lúc.</summary>
    public const string CodeIndexName = "IX_EquipmentTypes_Code";

    public const string NameIndexName = "IX_EquipmentTypes_CategoryId_NormalizedName";

    public Task<EquipmentType?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EquipmentTypes
            .Include(type => type.Category)
            .SingleOrDefaultAsync(type => type.Id == id, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default) =>
        dbContext.EquipmentTypes
            .AsNoTracking()
            .AnyAsync(type => type.Code == code && (excludeId == null || type.Id != excludeId), cancellationToken);

    public Task<bool> NameExistsAsync(
        Guid categoryId,
        string normalizedName,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default) =>
        dbContext.EquipmentTypes
            .AsNoTracking()
            .AnyAsync(
                type => type.CategoryId == categoryId
                    && type.NormalizedName == normalizedName
                    && (excludeId == null || type.Id != excludeId),
                cancellationToken);

    public Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        dbContext.Categories.SingleOrDefaultAsync(category => category.Id == categoryId, cancellationToken);

    public async Task AddAsync(EquipmentType equipmentType, CancellationToken cancellationToken = default) =>
        await dbContext.EquipmentTypes.AddAsync(equipmentType, cancellationToken);

    public async Task<EquipmentTypeSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return EquipmentTypeSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Version (xmin) đã đổi: quyết định của request này dựa trên dữ liệu cũ nên không được ghi.
            return EquipmentTypeSaveOutcome.ConcurrentUpdate;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CodeIndexName
        })
        {
            return EquipmentTypeSaveOutcome.DuplicateCode;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: NameIndexName
        })
        {
            return EquipmentTypeSaveOutcome.DuplicateName;
        }
    }

    public async Task<IReadOnlyList<EquipmentType>> ListPublishedAsync(
        Guid? categoryId = null,
        CancellationToken cancellationToken = default) =>
        await PublishedQuery()
            .Where(type => categoryId == null || type.CategoryId == categoryId)
            .OrderBy(type => type.Name)
            .ThenBy(type => type.Id)
            .ToListAsync(cancellationToken);

    public Task<EquipmentType?> GetPublishedAsync(Guid id, CancellationToken cancellationToken = default) =>
        PublishedQuery().SingleOrDefaultAsync(type => type.Id == id, cancellationToken);

    /// <summary>Bộ lọc duy nhất định nghĩa "công khai": type Published và category còn active.</summary>
    private IQueryable<EquipmentType> PublishedQuery() =>
        dbContext.EquipmentTypes
            .AsNoTracking()
            .Include(type => type.Category)
            .Where(type => type.Status == EquipmentTypeStatus.Published && type.Category.IsActive);
}
