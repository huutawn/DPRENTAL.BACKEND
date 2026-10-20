using Dprental.CatalogService.Common;
using System.Data;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class BrandRepository(CatalogServiceDbContext dbContext) : IBrandRepository
{
    public async Task<PagedResult<Brand>> SearchAsync(BrandSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var dbQuery = dbContext.Brands.AsNoTracking();

        if (query.IsActive.HasValue)
        {
            dbQuery = dbQuery.Where(b => b.IsActive == query.IsActive.Value);
        }

        var codeKeyword = query.Keyword is null ? null : CountRelationHelper.EscapeLikePattern(query.Keyword.Trim());
        var nameKeyword = query.NameKeyword is null ? codeKeyword : CountRelationHelper.EscapeLikePattern(query.NameKeyword.Trim());

        if (!string.IsNullOrWhiteSpace(codeKeyword) && !string.IsNullOrWhiteSpace(nameKeyword) && !string.Equals(codeKeyword, nameKeyword, StringComparison.Ordinal))
        {
            dbQuery = dbQuery.Where(b =>
                EF.Functions.ILike(b.Code, $"%{codeKeyword}%", "\\") ||
                EF.Functions.ILike(b.Name, $"%{nameKeyword}%", "\\"));
        }
        else if (!string.IsNullOrWhiteSpace(codeKeyword))
        {
            dbQuery = dbQuery.Where(b =>
                EF.Functions.ILike(b.Code, $"%{codeKeyword}%", "\\") ||
                EF.Functions.ILike(b.Name, $"%{codeKeyword}%", "\\"));
        }
        else if (!string.IsNullOrWhiteSpace(nameKeyword))
        {
            dbQuery = dbQuery.Where(b =>
                EF.Functions.ILike(b.Name, $"%{nameKeyword}%", "\\"));
        }

        if (query.ListingCounts != null)
        {
            var counts = CountRelationHelper.Query(dbContext, query.ListingCounts);
            var ranked = from entity in dbQuery
                         join count in counts on entity.Id equals count.Id into matches
                         from count in matches.DefaultIfEmpty()
                         select new { Entity = entity, ListingCount = (int?)count.ListingCount ?? 0 };
            var ordered = query.Sort?.Trim().ToLowerInvariant() switch
            {
                "listing_count_asc" => ranked.OrderBy(r => r.ListingCount == 0).ThenBy(r => r.ListingCount).ThenBy(r => r.Entity.Name).ThenBy(r => r.Entity.Id),
                "name_asc" => ranked.OrderBy(r => r.Entity.Name).ThenBy(r => r.Entity.Id),
                "name_desc" => ranked.OrderByDescending(r => r.Entity.Name).ThenBy(r => r.Entity.Id),
                "newest" => ranked.OrderByDescending(r => r.Entity.CreatedAt).ThenBy(r => r.Entity.Id),
                _ => ranked.OrderBy(r => r.ListingCount == 0).ThenByDescending(r => r.ListingCount).ThenBy(r => r.Entity.Name).ThenBy(r => r.Entity.Id)
            };
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            int total = await dbQuery.CountAsync(cancellationToken);
            var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
                .Select(r => r.Entity).ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PagedResult<Brand>(items, query.Page, query.PageSize, total);
        }

        var totalCountAdmin = await dbQuery.CountAsync(cancellationToken);

        string sortAdmin = query.Sort?.Trim().ToLowerInvariant() ?? string.Empty;
        dbQuery = sortAdmin switch
        {
            "name_asc" => dbQuery.OrderBy(b => b.Name).ThenBy(b => b.Id),
            "name_desc" => dbQuery.OrderByDescending(b => b.Name).ThenBy(b => b.Id),
            _ => dbQuery.OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id)
        };

        var skipAdmin = (query.Page - 1) * query.PageSize;
        var itemsAdmin = await dbQuery
            .Skip(skipAdmin)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Brand>(itemsAdmin, query.Page, query.PageSize, totalCountAdmin);
    }

    public async Task<Brand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await dbContext.Brands
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<Brand?> GetActiveByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await dbContext.Brands
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id && b.IsActive, cancellationToken);
    }

    public async Task<bool> IsCodeExistsAsync(string code, CancellationToken cancellationToken = default)
    {
        return await dbContext.Brands
            .AnyAsync(b => b.Code == code, cancellationToken);
    }

    public async Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default)
    {
        return await dbContext.Brands
            .AnyAsync(b => b.Slug == slug, cancellationToken);
    }

    public async Task<bool> IsCodeExistsAsync(string code, Guid excludeId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Brands
            .AnyAsync(b => b.Code == code && b.Id != excludeId, cancellationToken);
    }

    public async Task<bool> IsSlugExistsAsync(string slug, Guid excludeId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Brands
            .AnyAsync(b => b.Slug == slug && b.Id != excludeId, cancellationToken);
    }

    public async Task<Result<Brand>> AddAsync(Brand brand, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brand);

        try
        {
            await dbContext.Brands.AddAsync(brand, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(brand);
        }
        catch (DbUpdateException ex) when (IsDuplicateCodeViolation(ex))
        {
            return Result.Failure<Brand>(CatalogErrors.Brand.DuplicateCode);
        }
        catch (DbUpdateException ex) when (IsDuplicateSlugViolation(ex))
        {
            return Result.Failure<Brand>(CatalogErrors.Brand.DuplicateSlug);
        }
    }

    public async Task<Result<Brand>> UpdateAsync(Brand brand, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brand);

        try
        {
            dbContext.Brands.Update(brand);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(brand);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<Brand>(CatalogErrors.Brand.ConcurrencyConflict);
        }
        catch (DbUpdateException ex) when (IsDuplicateCodeViolation(ex))
        {
            return Result.Failure<Brand>(CatalogErrors.Brand.DuplicateCode);
        }
        catch (DbUpdateException ex) when (IsDuplicateSlugViolation(ex))
        {
            return Result.Failure<Brand>(CatalogErrors.Brand.DuplicateSlug);
        }
    }

    private static bool IsDuplicateCodeViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException postgresEx
            && postgresEx.SqlState == "23505"
            && (postgresEx.ConstraintName?.Contains("IX_Brands_Code", StringComparison.OrdinalIgnoreCase) == true
                || postgresEx.Message.Contains("IX_Brands_Code", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDuplicateSlugViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException postgresEx
            && postgresEx.SqlState == "23505"
            && (postgresEx.ConstraintName?.Contains("IX_Brands_Slug", StringComparison.OrdinalIgnoreCase) == true
                || postgresEx.Message.Contains("IX_Brands_Slug", StringComparison.OrdinalIgnoreCase));
    }
}
