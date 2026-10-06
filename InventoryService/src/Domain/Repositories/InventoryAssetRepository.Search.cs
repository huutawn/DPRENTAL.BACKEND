using System.Data;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

public sealed partial class InventoryAssetRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(
        OwnerAssetSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(criteria));
        }

        var (whereSql, parameters) = BuildOwnerPredicates(criteria, includeDynamicFilters: true);
        string orderSql = BuildOrderSql(criteria.Filters.Sort);

        parameters.Add(new NpgsqlParameter("pTake", criteria.Take));
        parameters.Add(new NpgsqlParameter("pSkip", criteria.Skip));

        string sql = $"""
            SELECT a.*, a.xmin FROM "inventory"."assets" a
            WHERE {whereSql}
            {orderSql}
            LIMIT @pTake OFFSET @pSkip
            """;

        return await _context.Assets
            .FromSqlRaw(sql, parameters.ToArray())
            .Include(a => a.RentalRate)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountForOwnerAsync(
        OwnerAssetSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(criteria));
        }

        var (whereSql, parameters) = BuildOwnerPredicates(criteria, includeDynamicFilters: true);

        string sql = $"""
            SELECT COUNT(*)::int AS "Value" FROM "inventory"."assets" a
            WHERE {whereSql}
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .SingleAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(
        OwnerAssetSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(criteria));
        }

        var (whereSql, parameters) = BuildOwnerPredicates(criteria, includeDynamicFilters: false);

        string sql = $"""
            SELECT DISTINCT a.schema_version AS "Value" FROM "inventory"."assets" a
            WHERE {whereSql}
            ORDER BY "Value" ASC
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetPublicByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        return await _context.Assets
            .Include(a => a.RentalRate)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.AcceptingBookings, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(
        AssetSearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var (whereSql, parameters) = BuildPublicDistinctVersionPredicates(filters);

        string sql = $"""
            SELECT DISTINCT a.schema_version AS "Value" FROM "inventory"."assets" a
            WHERE {whereSql}
            ORDER BY "Value" ASC
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PublicAssetSearchResult> SearchPublicAsync(
        PublicAssetSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (whereSql, parameters) = BuildPublicPredicates(criteria, includeDynamicFilters: true);
        string orderSql = BuildOrderSql(criteria.Filters.Sort);

        if (_context.Database.IsRelational())
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            int totalCount = await CountPublicInternalAsync(whereSql, CloneParameters(parameters), cancellationToken);

            var pageParameters = CloneParameters(parameters).ToList();
            pageParameters.Add(new NpgsqlParameter("pTake", criteria.Take));
            pageParameters.Add(new NpgsqlParameter("pSkip", criteria.Skip));

            var items = await ReadPublicPageInternalAsync(whereSql, orderSql, pageParameters.ToArray(), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PublicAssetSearchResult(items, totalCount);
        }
        else
        {
            int totalCount = await CountPublicInternalAsync(whereSql, CloneParameters(parameters), cancellationToken);

            var pageParameters = CloneParameters(parameters).ToList();
            pageParameters.Add(new NpgsqlParameter("pTake", criteria.Take));
            pageParameters.Add(new NpgsqlParameter("pSkip", criteria.Skip));

            var items = await ReadPublicPageInternalAsync(whereSql, orderSql, pageParameters.ToArray(), cancellationToken);

            return new PublicAssetSearchResult(items, totalCount);
        }
    }

    private async Task<int> CountPublicInternalAsync(
        string whereSql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT COUNT(*)::int AS "Value" FROM "inventory"."assets" a
            WHERE {whereSql}
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters)
            .SingleAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<InventoryAsset>> ReadPublicPageInternalAsync(
        string whereSql,
        string orderSql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT a.*, a.xmin FROM "inventory"."assets" a
            WHERE {whereSql}
            {orderSql}
            LIMIT @pTake OFFSET @pSkip
            """;

        return await _context.Assets
            .FromSqlRaw(sql, parameters)
            .Include(a => a.RentalRate)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
