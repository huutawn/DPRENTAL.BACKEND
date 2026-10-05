using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.CatalogService.Domain.Repositories;

public partial class EquipmentTypeRepository
{
    public async Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypeSpecSchemas.AsNoTracking()
            .Where(s => s.EquipmentTypeId == equipmentTypeId)
            .OrderBy(s => s.Version)
            .Select(s => new EquipmentTypeSpecSchemaDto(
                s.Id,
                s.EquipmentTypeId,
                s.Version,
                s.PublishedAt == null ? "Draft" : "Published",
                s.CreatedAt,
                s.PublishedAt,
                s.Definition))
            .ToListAsync(cancellationToken);
    }

    public async Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypeSpecSchemas.AsNoTracking()
            .Where(s => s.EquipmentTypeId == equipmentTypeId && s.Version == version)
            .Select(s => new EquipmentTypeSpecSchemaDto(
                s.Id,
                s.EquipmentTypeId,
                s.Version,
                s.PublishedAt == null ? "Draft" : "Published",
                s.CreatedAt,
                s.PublishedAt,
                s.Definition))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<EquipmentTypeSpecSchema?> GetDraftSpecSchemaEntityAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypeSpecSchemas
            .FirstOrDefaultAsync(s => s.EquipmentTypeId == equipmentTypeId && s.PublishedAt == null, cancellationToken);
    }

    public async Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EquipmentTypeSpecSchemas
            .FirstOrDefaultAsync(s => s.EquipmentTypeId == equipmentTypeId && s.Version == version, cancellationToken);
    }

    public async Task<int> GetMaxSpecSchemaVersionAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        var max = await dbContext.EquipmentTypeSpecSchemas.AsNoTracking()
            .Where(s => s.EquipmentTypeId == equipmentTypeId)
            .Select(s => (int?)s.Version)
            .MaxAsync(cancellationToken);

        return max ?? 0;
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> AddSpecSchemaDraftAsync(
        EquipmentType parent,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(definition);

        if (await dbContext.EquipmentTypeSpecSchemas.AnyAsync(s => s.EquipmentTypeId == parent.Id && s.PublishedAt == null, cancellationToken))
        {
            return Result.Failure<EquipmentTypeSpecSchemaDto>(CatalogErrors.EquipmentType.DraftAlreadyExists);
        }

        var maxVersion = await GetMaxSpecSchemaVersionAsync(parent.Id, cancellationToken);
        int nextVersion = maxVersion + 1;
        DateTime utcNow = DateTime.UtcNow;

        var schema = EquipmentTypeSpecSchema.CreateDraft(parent.Id, nextVersion, definition, utcNow);
        parent.Touch(utcNow);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            dbContext.Entry(parent).State = EntityState.Modified;
            await dbContext.SaveChangesAsync(cancellationToken);

            dbContext.EquipmentTypeSpecSchemas.Add(schema);
            parent.SpecSchemas.Add(schema);
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success(ToSpecSchemaDto(schema));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeSpecSchemaDto>(CatalogErrors.EquipmentType.ConcurrencyConflict);
        }
        catch (DbUpdateException ex) when (IsDraftOrVersionConstraintViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeSpecSchemaDto>(CatalogErrors.EquipmentType.DraftAlreadyExists);
        }
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema draftSchema,
        EquipmentTypeSpecDefinition newDefinition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(draftSchema);
        ArgumentNullException.ThrowIfNull(newDefinition);
        DateTime utcNow = DateTime.UtcNow;
        draftSchema.UpdateDefinition(newDefinition, utcNow);
        parent.Touch(utcNow);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            dbContext.Entry(parent).State = EntityState.Modified;
            dbContext.Entry(draftSchema).State = EntityState.Modified;
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success(ToSpecSchemaDto(draftSchema));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeSpecSchemaDto>(CatalogErrors.EquipmentType.ConcurrencyConflict);
        }
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(
        EquipmentType parent,
        EquipmentTypeSpecSchema draftSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(draftSchema);
        DateTime utcNow = DateTime.UtcNow;
        draftSchema.Publish(utcNow);
        parent.ApplyPublishedSchema(draftSchema, utcNow);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            dbContext.Entry(parent).State = EntityState.Modified;
            dbContext.Entry(draftSchema).State = EntityState.Modified;
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success(ToSpecSchemaDto(draftSchema));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<EquipmentTypeSpecSchemaDto>(CatalogErrors.EquipmentType.ConcurrencyConflict);
        }
    }

    internal static EquipmentTypeSpecSchemaDto ToSpecSchemaDto(EquipmentTypeSpecSchema schema) =>
        new(
            schema.Id,
            schema.EquipmentTypeId,
            schema.Version,
            schema.PublishedAt == null ? "Draft" : "Published",
            schema.CreatedAt,
            schema.PublishedAt,
            schema.Definition);

    private static bool IsDraftOrVersionConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == "23505"
        && (pg.ConstraintName?.Contains("Draft", StringComparison.OrdinalIgnoreCase) == true
            || pg.ConstraintName?.Contains("Version", StringComparison.OrdinalIgnoreCase) == true
            || pg.Message.Contains("Draft", StringComparison.OrdinalIgnoreCase));
}
