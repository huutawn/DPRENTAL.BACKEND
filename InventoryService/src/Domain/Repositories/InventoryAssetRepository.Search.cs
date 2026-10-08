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
            JOIN "inventory"."equipment_listings" l ON a.equipment_listing_id = l.id
            WHERE {whereSql}
            {orderSql}
            LIMIT @pTake OFFSET @pSkip
            """;

        return await _context.Assets
            .FromSqlRaw(sql, parameters.ToArray())
            .Include(a => a.Listing)
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
            JOIN "inventory"."equipment_listings" l ON a.equipment_listing_id = l.id
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
            SELECT DISTINCT l.schema_version AS "Value" FROM "inventory"."assets" a
            JOIN "inventory"."equipment_listings" l ON a.equipment_listing_id = l.id
            WHERE {whereSql}
            ORDER BY "Value" ASC
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);
    }
}
