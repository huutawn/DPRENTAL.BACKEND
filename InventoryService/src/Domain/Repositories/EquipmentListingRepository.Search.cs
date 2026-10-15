using System.Data;
using System.Globalization;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

public sealed partial class EquipmentListingRepository
{
    private sealed class ListingPageRow
    {
        public Guid Id { get; set; }
        public int TotalQuantity { get; set; }
        public int AvailableQuantity { get; set; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(
        AssetSearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var clauses = new List<string>
        {
            "l.status = 2" // Active
        };

        var parameters = new List<NpgsqlParameter>();

        string joinSql = "";
        if (filters.CandidateIds != null)
        {
            if (filters.CandidateIds.Count == 0)
            {
                return Array.Empty<int>();
            }
            joinSql = "JOIN unnest(@pCandidateIds) AS cand(id) ON l.id = cand.id";
            parameters.Add(new NpgsqlParameter("pCandidateIds", filters.CandidateIds.ToArray()));
        }
        else if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var escaped = EscapeLikePattern(filters.SearchKeyword);
            clauses.Add("(l.name ILIKE @pSearch ESCAPE '\\' OR l.model ILIKE @pSearch ESCAPE '\\')");
            parameters.Add(new NpgsqlParameter("pSearch", $"%{escaped}%"));
        }

        if (filters.EquipmentTypeId.HasValue)
        {
            clauses.Add("l.equipment_type_id = @pEquipmentTypeId");
            parameters.Add(new NpgsqlParameter("pEquipmentTypeId", filters.EquipmentTypeId.Value));
        }
        else if (filters.EquipmentTypeIds != null)
        {
            if (filters.EquipmentTypeIds.Count == 0)
            {
                clauses.Add("1 = 0");
            }
            else
            {
                clauses.Add("l.equipment_type_id = ANY(@pEquipmentTypeIds)");
                parameters.Add(new NpgsqlParameter("pEquipmentTypeIds", filters.EquipmentTypeIds.ToArray()));
            }
        }

        if (filters.BrandId.HasValue)
        {
            clauses.Add("l.brand_id = @pBrandId");
            parameters.Add(new NpgsqlParameter("pBrandId", filters.BrandId.Value));
        }

        string whereSql = string.Join(" AND ", clauses);
        string sql = $"""
            SELECT DISTINCT l.schema_version AS "Value" FROM "inventory"."equipment_listings" l
            {joinSql}
            WHERE {whereSql}
            ORDER BY "Value" ASC
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PublicEquipmentListingSearchResult> SearchPublicAsync(
        EquipmentListingSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (criteria.Filters.CandidateIds != null && criteria.Filters.CandidateIds.Count == 0)
        {
            return new PublicEquipmentListingSearchResult(Array.Empty<PublicEquipmentListingItem>(), 0);
        }

        if (_context.Database.IsRelational())
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var (countSql, pageSql, countParams, pageParams) = BuildSearchSql(criteria);

            int totalCount = await _context.Database
                .SqlQueryRaw<int>(countSql, countParams)
                .SingleAsync(cancellationToken);

            if (totalCount == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PublicEquipmentListingSearchResult(Array.Empty<PublicEquipmentListingItem>(), 0);
            }

            var rows = await _context.Database
                .SqlQueryRaw<ListingPageRow>(pageSql, pageParams)
                .ToListAsync(cancellationToken);
            var ids = rows.Select(r => r.Id).ToList();
            var listings = await _context.EquipmentListings
                .Where(l => ids.Contains(l.Id))
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var listingMap = listings.ToDictionary(l => l.Id);
            var items = rows
                .Where(r => listingMap.ContainsKey(r.Id))
                .Select(r => new PublicEquipmentListingItem(listingMap[r.Id], r.TotalQuantity, r.AvailableQuantity))
                .ToList();

            return new PublicEquipmentListingSearchResult(items, totalCount);
        }
        throw new NotSupportedException("Public equipment listing search requires PostgreSQL relational provider.");
    }

    private static (string CountSql, string PageSql, NpgsqlParameter[] CountParams, NpgsqlParameter[] PageParams) BuildSearchSql(
        EquipmentListingSearchCriteria criteria)
    {
        var clauses = new List<string>
        {
            "l.status = 2" // Active
        };

        var commonParams = new List<NpgsqlParameter>();

        var filters = criteria.Filters;
        bool hasCandidates = filters.CandidateIds != null && filters.CandidateIds.Count > 0;
        string joinSql = "";
        if (filters.CandidateIds != null)
        {
            if (filters.CandidateIds.Count == 0)
            {
                clauses.Add("1 = 0");
            }
            else
            {
                joinSql = "JOIN unnest(@pCandidateIds) WITH ORDINALITY AS cand(id, ord) ON l.id = cand.id";
                commonParams.Add(new NpgsqlParameter("pCandidateIds", filters.CandidateIds.ToArray()));
            }
        }
        else if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var escaped = EscapeLikePattern(filters.SearchKeyword);
            clauses.Add("(l.name ILIKE @pSearch ESCAPE '\\' OR l.model ILIKE @pSearch ESCAPE '\\')");
            commonParams.Add(new NpgsqlParameter("pSearch", $"%{escaped}%"));
        }

        if (filters.EquipmentTypeId.HasValue)
        {
            clauses.Add("l.equipment_type_id = @pEquipmentTypeId");
            commonParams.Add(new NpgsqlParameter("pEquipmentTypeId", filters.EquipmentTypeId.Value));
        }
        else if (filters.EquipmentTypeIds != null)
        {
            if (filters.EquipmentTypeIds.Count == 0)
            {
                clauses.Add("1 = 0");
            }
            else
            {
                clauses.Add("l.equipment_type_id = ANY(@pEquipmentTypeIds)");
                commonParams.Add(new NpgsqlParameter("pEquipmentTypeIds", filters.EquipmentTypeIds.ToArray()));
            }
        }

        if (filters.BrandId.HasValue)
        {
            clauses.Add("l.brand_id = @pBrandId");
            commonParams.Add(new NpgsqlParameter("pBrandId", filters.BrandId.Value));
        }

        AppendDynamicFilters(clauses, commonParams, filters);

        // Subquery expressions for total and available quantities
        string totalQtyExpr = """
            COALESCE((
                SELECT COUNT(*)::int FROM "inventory"."assets" a
                WHERE a.equipment_listing_id = l.id AND a.accepting_bookings = TRUE
            ), 0)
            """;

        string availableQtyExpr;
        bool hasInterval = criteria.AvailabilityFrom.HasValue && criteria.AvailabilityTo.HasValue;

        if (hasInterval)
        {
            var today = BusinessDateTime.ToVietnamDate(criteria.AvailabilityEvaluationUtc);
            commonParams.Add(new NpgsqlParameter("pToday", today));
            commonParams.Add(new NpgsqlParameter("pAvailFrom", criteria.AvailabilityFrom!.Value));
            commonParams.Add(new NpgsqlParameter("pAvailTo", criteria.AvailabilityTo!.Value));
            commonParams.Add(new NpgsqlParameter("pAllocatedStatus", (int)AllocationStatus.Allocated));
            commonParams.Add(new NpgsqlParameter("pHeldStatus", (int)AllocationStatus.Held));
            commonParams.Add(new NpgsqlParameter("pEvaluationUtc", criteria.AvailabilityEvaluationUtc));

            availableQtyExpr = """
                COALESCE((
                    SELECT COUNT(*)::int FROM "inventory"."assets" a
                    WHERE a.equipment_listing_id = l.id
                      AND a.accepting_bookings = TRUE
                      AND NOT EXISTS (
                          SELECT 1 FROM "inventory"."calendar_blocks" cb
                          WHERE cb.asset_id = a.id
                            AND cb.start_date <= @pAvailTo
                            AND cb.end_date >= @pAvailFrom
                      )
                      AND NOT EXISTS (
                          SELECT 1 FROM "inventory"."asset_allocations" al
                          WHERE al.asset_id = a.id
                            AND (
                                (al.start_date <= @pAvailTo AND al.end_date >= @pAvailFrom
                                 AND (al.status = @pAllocatedStatus OR (al.status = @pHeldStatus AND al.hold_expires_at > @pEvaluationUtc)))
                                OR
                                (al.status = @pAllocatedStatus
                                 AND al.handed_over_at IS NOT NULL
                                 AND al.returned_at IS NULL
                                 AND al.end_date < @pToday
                                 AND @pAvailTo >= al.start_date)
                            )
                      )
                ), 0)
                """;
        }
        else
        {
            var today = BusinessDateTime.ToVietnamDate(criteria.AvailabilityEvaluationUtc);
            commonParams.Add(new NpgsqlParameter("pToday", today));
            commonParams.Add(new NpgsqlParameter("pAllocatedStatus", (int)AllocationStatus.Allocated));
            commonParams.Add(new NpgsqlParameter("pHeldStatus", (int)AllocationStatus.Held));
            commonParams.Add(new NpgsqlParameter("pEvaluationUtc", criteria.AvailabilityEvaluationUtc));

            availableQtyExpr = """
                COALESCE((
                    SELECT COUNT(*)::int FROM "inventory"."assets" a
                    WHERE a.equipment_listing_id = l.id
                      AND a.accepting_bookings = TRUE
                      AND NOT EXISTS (
                          SELECT 1 FROM "inventory"."calendar_blocks" cb
                          WHERE cb.asset_id = a.id
                            AND cb.start_date <= @pToday
                            AND cb.end_date >= @pToday
                      )
                      AND NOT EXISTS (
                          SELECT 1 FROM "inventory"."asset_allocations" al
                          WHERE al.asset_id = a.id
                            AND (
                                (al.start_date <= @pToday AND al.end_date >= @pToday
                                 AND (al.status = @pAllocatedStatus OR (al.status = @pHeldStatus AND al.hold_expires_at > @pEvaluationUtc)))
                                OR
                                (al.status = @pAllocatedStatus
                                 AND al.handed_over_at IS NOT NULL
                                 AND al.returned_at IS NULL
                                 AND al.end_date < @pToday
                                 AND al.start_date <= @pToday)
                            )
                      )
                ), 0)
                """;
        }

        string baseWhere = string.Join(" AND ", clauses);

        // When interval is provided, filter availableQuantity >= requestedQuantity (default 1)
        string filterWhere = baseWhere;
        if (hasInterval)
        {
            int reqQty = criteria.RequestedQuantity ?? 1;
            commonParams.Add(new NpgsqlParameter("pReqQty", reqQty));
            filterWhere = $"{baseWhere} AND ({availableQtyExpr}) >= @pReqQty";
        }

        string countSql = $"""
            SELECT COUNT(*)::int AS "Value" FROM "inventory"."equipment_listings" l
            {joinSql}
            WHERE {filterWhere}
            """;

        string orderSql = criteria.Filters.Sort switch
        {
            AssetSortOption.Relevance when hasCandidates => "ORDER BY cand.ord ASC",
            AssetSortOption.Relevance => "ORDER BY l.created_at DESC, l.id DESC",
            AssetSortOption.CreatedAsc => "ORDER BY l.created_at ASC, l.id ASC",
            AssetSortOption.ModelDesc => "ORDER BY l.model DESC, l.id DESC",
            AssetSortOption.ModelAsc => "ORDER BY l.model ASC, l.id ASC",
            _ => "ORDER BY l.created_at DESC, l.id DESC"
        };

        string pageSql = $"""
            SELECT l.id AS "Id", ({totalQtyExpr}) AS "TotalQuantity", ({availableQtyExpr}) AS "AvailableQuantity"
            FROM "inventory"."equipment_listings" l
            {joinSql}
            WHERE {filterWhere}
            {orderSql}
            LIMIT @pTake OFFSET @pSkip
            """;

        var countParams = CloneParameters(commonParams);

        var pageParamsList = commonParams.ToList();
        pageParamsList.Add(new NpgsqlParameter("pTake", criteria.Take));
        pageParamsList.Add(new NpgsqlParameter("pSkip", criteria.Skip));
        var pageParams = CloneParameters(pageParamsList);

        return (countSql, pageSql, countParams, pageParams);
    }


    private static void AppendDynamicFilters(
        List<string> clauses,
        List<NpgsqlParameter> parameters,
        AssetSearchFilters filters)
    {
        if (filters.DynamicFilters == null || filters.DynamicFilters.Count == 0)
        {
            return;
        }

        if (filters.CompatibleSchemaVersions == null || filters.CompatibleSchemaVersions.Count == 0)
        {
            clauses.Add("1 = 0");
            return;
        }

        clauses.Add("l.schema_version = ANY(@pCompatibleVersions)");
        parameters.Add(new NpgsqlParameter("pCompatibleVersions", filters.CompatibleSchemaVersions.ToArray()));

        for (int i = 0; i < filters.DynamicFilters.Count; i++)
        {
            var filter = filters.DynamicFilters[i];
            string keyParam = $"pKey_{i}";
            parameters.Add(new NpgsqlParameter(keyParam, filter.Field));

            string valParam = $"pVal_{i}";

            if (string.Equals(filter.DataType, "number", StringComparison.OrdinalIgnoreCase))
            {
                string op = filter.Operator switch
                {
                    "eq" => "=",
                    "gt" => ">",
                    "gte" => ">=",
                    "lt" => "<",
                    "lte" => "<=",
                    _ => throw new ArgumentException($"Toán tử không hợp lệ: {filter.Operator}")
                };

                decimal numVal = Convert.ToDecimal(filter.Value, CultureInfo.InvariantCulture);
                parameters.Add(new NpgsqlParameter(valParam, numVal));

                clauses.Add($"(CASE WHEN jsonb_typeof(l.spec_values -> @{keyParam}) = 'number' THEN (l.spec_values ->> @{keyParam})::numeric ELSE NULL END) {op} @{valParam}");
            }
            else if (string.Equals(filter.DataType, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                bool boolVal = Convert.ToBoolean(filter.Value, CultureInfo.InvariantCulture);
                parameters.Add(new NpgsqlParameter(valParam, boolVal));

                clauses.Add($"jsonb_typeof(l.spec_values -> @{keyParam}) = 'boolean' AND (l.spec_values -> @{keyParam}) = to_jsonb(@{valParam}::boolean)");
            }
            else
            {
                string strVal = filter.Value?.ToString() ?? string.Empty;
                parameters.Add(new NpgsqlParameter(valParam, strVal));

                clauses.Add($"jsonb_typeof(l.spec_values -> @{keyParam}) = 'string' AND (l.spec_values ->> @{keyParam}) = @{valParam}");
            }
        }
    }

    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
    }

    private static NpgsqlParameter[] CloneParameters(IEnumerable<NpgsqlParameter> parameters)
    {
        return parameters.Select(p => new NpgsqlParameter(p.ParameterName, p.Value)).ToArray();
    }
}
