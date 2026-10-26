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

        if (criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count == 0)
        {
            return Array.Empty<InventoryAsset>();
        }

        var (whereSql, parameters) = BuildOwnerPredicates(criteria, includeDynamicFilters: true);
        bool hasCandidates = criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count > 0;
        string joinSql = "";
        if (hasCandidates)
        {
            joinSql = "JOIN unnest(@pCandidateIds) WITH ORDINALITY AS cand(id, ord) ON a.id = cand.id";
            parameters.Add(new NpgsqlParameter("pCandidateIds", criteria.Filters.CandidateIds!.ToArray()));
        }

        string orderSql;
        if (criteria.Filters.Sort == AssetSortOption.Relevance)
        {
            bool hasKeyword = !string.IsNullOrWhiteSpace(criteria.Filters.SearchKeyword);
            if (hasKeyword)
            {
                string kw = criteria.Filters.SearchKeyword!.Trim();
                string exact = kw.ToLowerInvariant();
                string phrase = $"%{EscapeLikePattern(kw)}%";
                parameters.Add(new NpgsqlParameter("pExact", exact));
                parameters.Add(new NpgsqlParameter("pPhrase", phrase));

                if (hasCandidates)
                {
                    orderSql = """
                        ORDER BY
                            CASE
                                WHEN LOWER(l.name) = @pExact OR LOWER(l.model) = @pExact OR (a.serial_number IS NOT NULL AND LOWER(a.serial_number) = @pExact) THEN 1
                                WHEN l.name ILIKE @pPhrase ESCAPE '\' OR l.model ILIKE @pPhrase ESCAPE '\' OR (a.serial_number IS NOT NULL AND a.serial_number ILIKE @pPhrase ESCAPE '\') THEN 2
                                ELSE 3
                            END ASC,
                            cand.ord ASC,
                            a.id ASC
                        """;
                }
                else
                {
                    orderSql = """
                        ORDER BY
                            CASE
                                WHEN LOWER(l.name) = @pExact OR LOWER(l.model) = @pExact OR (a.serial_number IS NOT NULL AND LOWER(a.serial_number) = @pExact) THEN 1
                                WHEN l.name ILIKE @pPhrase ESCAPE '\' OR l.model ILIKE @pPhrase ESCAPE '\' OR (a.serial_number IS NOT NULL AND a.serial_number ILIKE @pPhrase ESCAPE '\') THEN 2
                                ELSE 3
                            END ASC,
                            a.created_at DESC,
                            a.id DESC
                        """;
                }
            }
            else
            {
                orderSql = hasCandidates
                    ? "ORDER BY cand.ord ASC, a.id ASC"
                    : "ORDER BY a.created_at DESC, a.id DESC";
            }
        }
        else
        {
            orderSql = criteria.Filters.Sort switch
            {
                AssetSortOption.CreatedAsc => "ORDER BY a.created_at ASC, a.id ASC",
                AssetSortOption.ModelDesc => "ORDER BY l.model DESC, a.id DESC",
                AssetSortOption.ModelAsc => "ORDER BY l.model ASC, a.id ASC",
                AssetSortOption.ManufactureYearDesc => "ORDER BY a.manufacture_year DESC NULLS LAST, a.id DESC",
                AssetSortOption.ManufactureYearAsc => "ORDER BY a.manufacture_year ASC NULLS LAST, a.id ASC",
                _ => "ORDER BY a.created_at DESC, a.id DESC"
            };
        }

        parameters.Add(new NpgsqlParameter("pTake", criteria.Take));
        parameters.Add(new NpgsqlParameter("pSkip", criteria.Skip));

        string sql = $"""
            SELECT a.*, a.xmin FROM "inventory"."assets" a
            JOIN "inventory"."equipment_listings" l ON a.equipment_listing_id = l.id
            {joinSql}
            WHERE {whereSql}
            {orderSql}
            LIMIT @pTake OFFSET @pSkip
            """;

        return await _context.Assets
            .FromSqlRaw(sql, parameters.ToArray())
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

        if (criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count == 0)
        {
            return 0;
        }

        var (whereSql, parameters) = BuildOwnerPredicates(criteria, includeDynamicFilters: true);
        string joinSql = "";
        if (criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count > 0)
        {
            joinSql = "JOIN unnest(@pCandidateIds) AS cand(id) ON a.id = cand.id";
            parameters.Add(new NpgsqlParameter("pCandidateIds", criteria.Filters.CandidateIds.ToArray()));
        }

        string sql = $"""
            SELECT COUNT(*)::int AS "Value" FROM "inventory"."assets" a
            JOIN "inventory"."equipment_listings" l ON a.equipment_listing_id = l.id
            {joinSql}
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

        if (criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        var (whereSql, parameters) = BuildOwnerPredicates(criteria, includeDynamicFilters: false);
        string joinSql = "";
        if (criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count > 0)
        {
            joinSql = "JOIN unnest(@pCandidateIds) AS cand(id) ON a.id = cand.id";
            parameters.Add(new NpgsqlParameter("pCandidateIds", criteria.Filters.CandidateIds.ToArray()));
        }

        string sql = $"""
            SELECT DISTINCT l.schema_version AS "Value" FROM "inventory"."assets" a
            JOIN "inventory"."equipment_listings" l ON a.equipment_listing_id = l.id
            {joinSql}
            WHERE {whereSql}
            ORDER BY "Value" ASC
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);
    }
}
