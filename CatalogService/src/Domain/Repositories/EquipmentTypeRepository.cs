using System.Linq.Expressions;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.CatalogService.Domain.Repositories;

public partial class EquipmentTypeRepository(CatalogServiceDbContext dbContext) : IEquipmentTypeRepository
{
    private static readonly Expression<Func<EquipmentType, EquipmentTypeDetailDto>> DetailSelector = e =>
        new EquipmentTypeDetailDto(
            e.Id,
            e.Code,
            e.Name,
            e.Description,
            e.Status,
            e.CreatedAt,
            e.UpdatedAt,
            e.PublishedAt,
            e.Categories
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Select(c => new CategoryDto(c.Id, c.Name, c.ImageMediaId, null))
                .ToList(),
            e.Media
                .OrderBy(m => m.DisplayOrder)
                .ThenBy(m => m.Id)
                .Select(m => new EquipmentTypeMediaResponseDto(m.MediaId, null, m.DisplayOrder, m.IsThumbnail))
                .ToList(),
            e.CurrentSchemaVersion,
            e.SpecSchemas
                .Where(s => s.Version == e.CurrentSchemaVersion)
                .Select(s => new EquipmentTypeSpecSchemaDto(
                    s.Id,
                    s.EquipmentTypeId,
                    s.Version,
                    s.PublishedAt == null ? "Draft" : "Published",
                    s.CreatedAt,
                    s.PublishedAt,
                    s.Definition))
                .FirstOrDefault());

    public async Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(
        GetEquipmentTypesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = dbContext.EquipmentTypes.AsNoTracking()
            .Where(e => e.Status == EquipmentTypeStatus.Published);

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            query = query.Where(e =>
                EF.Functions.ILike(e.Code, $"%{keyword}%") ||
                EF.Functions.ILike(e.Name, $"%{keyword}%") ||
                (e.Description != null && EF.Functions.ILike(e.Description, $"%{keyword}%")));
        }

        if (request.CategoryId.HasValue)
        {
            query = query.Where(e => e.Categories.Any(c => c.Id == request.CategoryId.Value));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        string sort = request.Sort?.Trim().ToLowerInvariant() ?? string.Empty;
        query = sort switch
        {
            "name_asc" => query.OrderBy(e => e.Name).ThenBy(e => e.Id),
            "name_desc" => query.OrderByDescending(e => e.Name).ThenBy(e => e.Id),
            _ => query.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
        };

        var skip = (request.Page - 1) * request.PageSize;
        var items = await query
            .Skip(skip)
            .Take(request.PageSize)
            .Select(e => new EquipmentTypeListItemDto(
                e.Id,
                e.Code,
                e.Name,
                e.Description,
                e.Categories
                    .OrderBy(c => c.Name)
                    .ThenBy(c => c.Id)
                    .Select(c => new CategoryDto(c.Id, c.Name, c.ImageMediaId, null))
                    .ToList(),
                e.Media
                    .OrderByDescending(m => m.IsThumbnail)
                    .ThenBy(m => m.DisplayOrder)
                    .ThenBy(m => m.Id)
                    .Select(m => (Guid?)m.MediaId)
                    .FirstOrDefault(),
                null,
                e.CurrentSchemaVersion
            ))
            .ToListAsync(cancellationToken);

        return new PagedResult<EquipmentTypeListItemDto>(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var detail = await dbContext.EquipmentTypes.AsNoTracking()
            .Where(e => e.Id == id && e.Status == EquipmentTypeStatus.Published)
            .Select(DetailSelector)
            .FirstOrDefaultAsync(cancellationToken);

        return detail is null ? null : detail with
        {
            PrimarySpecs = EquipmentTypeDetailDto.ExtractPrimarySpecs(detail.SpecSchema?.Definition)
        };
    }

    public async Task<EquipmentType?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypes
            .Include(e => e.Categories)
            .Include(e => e.Media)
            .Include(e => e.SpecSchemas)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var detail = await dbContext.EquipmentTypes.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(DetailSelector)
            .FirstOrDefaultAsync(cancellationToken);

        return detail is null ? null : detail with
        {
            PrimarySpecs = EquipmentTypeDetailDto.ExtractPrimarySpecs(detail.SpecSchema?.Definition)
        };
    }

    public async Task<EquipmentTypeMetadataDto?> GetMetadataByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypes.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new EquipmentTypeMetadataDto(e.Id, e.Status, e.CurrentSchemaVersion))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ExistsByCodeAsync(
        string code,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypes.AsNoTracking()
            .AnyAsync(e => e.Code == code && (excludeId == null || e.Id != excludeId.Value), cancellationToken);
    }

    public async Task<Result<EquipmentTypeDetailDto>> AddAsync(
        EquipmentType equipmentType,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems,
        EquipmentTypeSpecDefinition? initialSpecSchema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(equipmentType);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var category in equipmentType.Categories)
            {
                if (dbContext.Entry(category).State == EntityState.Detached)
                {
                    dbContext.Attach(category);
                }
            }

            dbContext.EquipmentTypes.Add(equipmentType);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (initialSpecSchema != null)
            {
                var schema = EquipmentTypeSpecSchema.CreateDraft(
                    equipmentType.Id,
                    1,
                    initialSpecSchema,
                    equipmentType.CreatedAt);
                dbContext.EquipmentTypeSpecSchemas.Add(schema);
                equipmentType.SpecSchemas.Add(schema);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            if (mediaItems != null && mediaItems.Count > 0)
            {
                foreach (var item in mediaItems)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    var media = new EquipmentTypeMedia
                    {
                        Id = Guid.NewGuid(),
                        EquipmentTypeId = equipmentType.Id,
                        MediaId = item.MediaId,
                        DisplayOrder = item.DisplayOrder,
                        IsThumbnail = item.IsThumbnail
                    };
                    dbContext.EquipmentTypeMedia.Add(media);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Result.Success(ToDetailDto(equipmentType));
        }
        catch (DbUpdateException ex) when (IsDuplicateCodeViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeDetailDto>(CatalogErrors.EquipmentType.DuplicateCode);
        }
    }

    public async Task<Result<EquipmentTypeDetailDto>> SaveAsync(
        EquipmentType entity,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var category in entity.Categories)
            {
                if (dbContext.Entry(category).State == EntityState.Detached)
                {
                    dbContext.Attach(category);
                }
            }

            // 1. Ghi parent với original xmin trước khi thay đổi media, kể cả media-only PATCH
            dbContext.Entry(entity).State = EntityState.Modified;
            await dbContext.SaveChangesAsync(cancellationToken);

            // 2. Clear thumbnail cũ và reconcile media nếu có thay đổi media
            if (desiredMedia != null)
            {
                // Clear thumbnail cũ trước
                foreach (var m in entity.Media)
                {
                    m.IsThumbnail = false;
                }
                await dbContext.SaveChangesAsync(cancellationToken);

                if (desiredMedia.Count == 0)
                {
                    dbContext.EquipmentTypeMedia.RemoveRange(entity.Media);
                    entity.Media.Clear();
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    var newMediaMap = desiredMedia
                        .Where(m => m != null)
                        .ToDictionary(m => m!.MediaId, m => m!);

                    var toRemove = entity.Media.Where(m => !newMediaMap.ContainsKey(m.MediaId)).ToList();
                    foreach (var rem in toRemove)
                    {
                        dbContext.EquipmentTypeMedia.Remove(rem);
                    }

                    var existingMap = entity.Media.ToDictionary(m => m.MediaId);
                    foreach (var item in desiredMedia)
                    {
                        if (item == null)
                        {
                            continue;
                        }

                        if (existingMap.TryGetValue(item.MediaId, out var existing))
                        {
                            existing.DisplayOrder = item.DisplayOrder;
                            existing.IsThumbnail = item.IsThumbnail;
                        }
                        else
                        {
                            var newMedia = new EquipmentTypeMedia
                            {
                                Id = Guid.NewGuid(),
                                EquipmentTypeId = entity.Id,
                                MediaId = item.MediaId,
                                DisplayOrder = item.DisplayOrder,
                                IsThumbnail = item.IsThumbnail
                            };
                            dbContext.EquipmentTypeMedia.Add(newMedia);
                        }
                    }
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            // 3. Tạo response từ trạng thái aggregate của operation vừa lưu, tránh đọc lại sau commit
            return Result.Success(ToDetailDto(entity));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeDetailDto>(CatalogErrors.EquipmentType.ConcurrencyConflict);
        }
        catch (DbUpdateException ex) when (IsDuplicateCodeViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeDetailDto>(CatalogErrors.EquipmentType.DuplicateCode);
        }
    }

    public async Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(
        EquipmentType entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        try
        {
            dbContext.Entry(entity).State = EntityState.Modified;
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(ToDetailDto(entity));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<EquipmentTypeDetailDto>(CatalogErrors.EquipmentType.ConcurrencyConflict);
        }
        catch (DbUpdateException ex) when (IsDuplicateCodeViolation(ex))
        {
            return Result.Failure<EquipmentTypeDetailDto>(CatalogErrors.EquipmentType.DuplicateCode);
        }
    }

    private static EquipmentTypeDetailDto ToDetailDto(EquipmentType entity)
    {
        EquipmentTypeSpecSchemaDto? currentSpecSchema = null;
        if (entity.CurrentSchemaVersion.HasValue)
        {
            var currentSchemaEntity = entity.SpecSchemas.FirstOrDefault(s => s.Version == entity.CurrentSchemaVersion.Value);
            if (currentSchemaEntity != null)
            {
                currentSpecSchema = ToSpecSchemaDto(currentSchemaEntity);
            }
        }

        return new EquipmentTypeDetailDto(
            entity.Id,
            entity.Code,
            entity.Name,
            entity.Description,
            entity.Status,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.PublishedAt,
            entity.Categories
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Select(c => new CategoryDto(c.Id, c.Name, c.ImageMediaId, null))
                .ToList(),
            entity.Media
                .DistinctBy(m => m.Id)
                .OrderBy(m => m.DisplayOrder)
                .ThenBy(m => m.Id)
                .Select(m => new EquipmentTypeMediaResponseDto(m.MediaId, null, m.DisplayOrder, m.IsThumbnail))
                .ToList(),
            entity.CurrentSchemaVersion,
            currentSpecSchema,
            EquipmentTypeDetailDto.ExtractPrimarySpecs(currentSpecSchema?.Definition));
    }

    private static bool IsDuplicateCodeViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException postgresEx
            && postgresEx.SqlState == "23505"
            && (postgresEx.ConstraintName?.Contains("IX_EquipmentTypes_Code", StringComparison.OrdinalIgnoreCase) == true
                || postgresEx.Message.Contains("IX_EquipmentTypes_Code", StringComparison.OrdinalIgnoreCase));
    }
}
