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
        public string? EffectiveTier { get; set; }
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
        // Không có ứng viên chỉ mục: mỗi từ phải xuất hiện trong name hoặc model (AND giữa các từ).
        // Escape ký tự LIKE để %, _ và dấu gạch chéo trong đầu vào được tìm như văn bản.
        else if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var words = filters.SearchKeyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string pName = $"pWord_{i}";
                var escaped = EscapeLikePattern(words[i]);
                clauses.Add($"(l.name ILIKE @{pName} ESCAPE '\\' OR l.model ILIKE @{pName} ESCAPE '\\')");
                parameters.Add(new NpgsqlParameter(pName, $"%{escaped}%"));
            }
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

        string warehouseJoin = "";
        if (!string.IsNullOrWhiteSpace(filters.ProvinceCode) || !string.IsNullOrWhiteSpace(filters.WardCode))
        {
            warehouseJoin = "JOIN \"inventory\".\"warehouses\" w ON l.owner_user_id = w.owner_user_id";
            if (!string.IsNullOrWhiteSpace(filters.ProvinceCode))
            {
                clauses.Add("w.province_code = @pProvinceCode");
                parameters.Add(new NpgsqlParameter("pProvinceCode", filters.ProvinceCode.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(filters.WardCode))
            {
                clauses.Add("w.ward_code = @pWardCode");
                parameters.Add(new NpgsqlParameter("pWardCode", filters.WardCode.Trim()));
            }
        }

        string whereSql = string.Join(" AND ", clauses);
        string sql = $"""
            SELECT DISTINCT l.schema_version AS "Value" FROM "inventory"."equipment_listings" l
            {joinSql}
            {warehouseJoin}
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
            // COUNT, trang kết quả và dữ liệu listing phải đọc cùng snapshot khi có cập nhật đồng thời.
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var (countSql, pageSql, holdExpirySql, countParams, pageParams, holdParams) = BuildSearchSql(criteria);

            int totalCount = await _context.Database
                .SqlQueryRaw<int>(countSql, countParams)
                .SingleAsync(cancellationToken);

            DateTimeOffset? nextAvailabilityChangeAtUtc = null;
            if (holdExpirySql != null && holdParams != null)
            {
                nextAvailabilityChangeAtUtc = await _context.Database
                    .SqlQueryRaw<DateTimeOffset?>(holdExpirySql, holdParams)
                    .SingleOrDefaultAsync(cancellationToken);
            }

            if (totalCount == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PublicEquipmentListingSearchResult(Array.Empty<PublicEquipmentListingItem>(), 0, nextAvailabilityChangeAtUtc);
            }

            var rows = await _context.Database
                .SqlQueryRaw<ListingPageRow>(pageSql, pageParams)
                .ToListAsync(cancellationToken);
            var ids = rows.Select(r => r.Id).ToList();
            var listings = await _context.EquipmentListings
                .Include(l => l.Warehouse)
                .Where(l => ids.Contains(l.Id))
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var listingMap = listings.ToDictionary(l => l.Id);
            var items = rows
                .Where(r => listingMap.ContainsKey(r.Id))
                .Select(r => new PublicEquipmentListingItem(listingMap[r.Id], r.TotalQuantity, r.AvailableQuantity))
                .ToList();
            string? effectiveTier = rows.Count > 0 ? rows[0].EffectiveTier : null;

            return new PublicEquipmentListingSearchResult(items, totalCount, nextAvailabilityChangeAtUtc, effectiveTier);
        }
        throw new NotSupportedException("Public equipment listing search requires PostgreSQL relational provider.");
    }

    private static (string CountSql, string PageSql, string? HoldExpirySql, NpgsqlParameter[] CountParams, NpgsqlParameter[] PageParams, NpgsqlParameter[]? HoldParams) BuildSearchSql(
        EquipmentListingSearchCriteria criteria)
    {
        var clauses = new List<string>
        {
            "l.status = 2" // Active
        };

        var commonParams = new List<NpgsqlParameter>();

        if (criteria.ExcludedOwnerUserId.HasValue && criteria.ExcludedOwnerUserId.Value != Guid.Empty)
        {
            clauses.Add("l.owner_user_id <> @pExcludedOwnerUserId");
            commonParams.Add(new NpgsqlParameter("pExcludedOwnerUserId", criteria.ExcludedOwnerUserId.Value));
        }

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
                // Giữ thứ tự liên quan từ Meilisearch để dùng khi sort=relevance; SQL vẫn lọc lại.
                joinSql = "JOIN unnest(@pCandidateIds) WITH ORDINALITY AS cand(id, ord) ON l.id = cand.id";
                commonParams.Add(new NpgsqlParameter("pCandidateIds", filters.CandidateIds.ToArray()));
            }
        }
        // Không có ứng viên chỉ mục: mỗi từ phải xuất hiện trong name hoặc model (AND giữa các từ).
        // Escape ký tự LIKE để %, _ và dấu gạch chéo trong đầu vào được tìm như văn bản.
        else if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var words = filters.SearchKeyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string pName = $"pWord_{i}";
                var escaped = EscapeLikePattern(words[i]);
                clauses.Add($"(l.name ILIKE @{pName} ESCAPE '\\' OR l.model ILIKE @{pName} ESCAPE '\\')");
                commonParams.Add(new NpgsqlParameter(pName, $"%{escaped}%"));
            }
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

        string warehouseJoin = "";
        if (!string.IsNullOrWhiteSpace(filters.ProvinceCode) || !string.IsNullOrWhiteSpace(filters.WardCode))
        {
            warehouseJoin = "JOIN \"inventory\".\"warehouses\" w ON l.owner_user_id = w.owner_user_id";
            if (!string.IsNullOrWhiteSpace(filters.ProvinceCode))
            {
                clauses.Add("w.province_code = @pProvinceCode");
                commonParams.Add(new NpgsqlParameter("pProvinceCode", filters.ProvinceCode.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(filters.WardCode))
            {
                clauses.Add("w.ward_code = @pWardCode");
                commonParams.Add(new NpgsqlParameter("pWardCode", filters.WardCode.Trim()));
            }
        }

        AppendDynamicFilters(clauses, commonParams, filters);

        // Tổng lượng chỉ gồm asset đang nhận đặt thuê. Lượng trống loại lịch chặn,
        // phân bổ trùng ngày, hold còn hạn và thiết bị đã bàn giao quá hạn nhưng chưa trả.
        // Nếu không truyền khoảng thuê, đánh giá khả dụng trong ngày hiện tại ở Việt Nam.
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

        // Có khoảng thuê thì chỉ giữ listing đủ số lượng yêu cầu, mặc định ít nhất một thiết bị.
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
            {warehouseJoin}
            WHERE {filterWhere}
            """;

        var pageParamsList = commonParams.ToList();
        pageParamsList.Add(new NpgsqlParameter("pTake", criteria.Take));
        pageParamsList.Add(new NpgsqlParameter("pSkip", criteria.Skip));

        string pageSql;
        if (criteria.Filters.Sort == AssetSortOption.Recommended)
        {
            var personalTypeIds = criteria.PersonalScores?.Select(s => s.EquipmentTypeId).ToArray() ?? Array.Empty<Guid>();
            var personalScores = criteria.PersonalScores?.Select(s => s.Score).ToArray() ?? Array.Empty<long>();
            var globalListingIds = criteria.GlobalScores?.Select(s => s.ListingId).ToArray() ?? Array.Empty<Guid>();
            var globalScores = criteria.GlobalScores?.Select(s => s.Score).ToArray() ?? Array.Empty<long>();
            string effectiveSeed = !string.IsNullOrWhiteSpace(criteria.Seed) ? criteria.Seed : "default_seed";

            pageParamsList.Add(new NpgsqlParameter("pPersonalTypeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = personalTypeIds });
            pageParamsList.Add(new NpgsqlParameter("pPersonalScores", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) { Value = personalScores });
            pageParamsList.Add(new NpgsqlParameter("pGlobalListingIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = globalListingIds });
            pageParamsList.Add(new NpgsqlParameter("pGlobalScores", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) { Value = globalScores });
            pageParamsList.Add(new NpgsqlParameter("pSeed", effectiveSeed));

            // Chọn tier trên TOÀN BỘ tập hợp sau lọc, trước LIMIT/OFFSET để không đổi tier theo trang.
            // personalized: weight = 1 + 12 * interest/max_interest + 4 * popularity/max_popularity.
            // global: weight = 1 + 8 * popularity/max_popularity. Listing không có điểm vẫn có weight=1.
            // Hash(seed, id) tạo u trong (0,1); sắp -ln(u)/weight tăng dần tạo thứ tự giả ngẫu nhiên
            // có trọng số: điểm cao có cơ hội đứng đầu lớn hơn, không phải luôn đứng trước điểm thấp.
            // Không có tín hiệu phù hợp thì dùng created_at DESC, id DESC; ID phá hòa để phân trang ổn định.
            pageSql = $"""
                WITH valid_listings AS (
                    SELECT
                        l.id,
                        l.created_at,
                        l.updated_at,
                        COALESCE(pers.score, 0) AS interest,
                        COALESCE(glob.score, 0) AS popularity
                    FROM "inventory"."equipment_listings" l
                    {joinSql}
                    {warehouseJoin}
                    LEFT JOIN (
                        SELECT id, score FROM unnest(@pPersonalTypeIds, @pPersonalScores) AS p(id, score)
                    ) pers ON l.equipment_type_id = pers.id
                    LEFT JOIN (
                        SELECT id, score FROM unnest(@pGlobalListingIds, @pGlobalScores) AS g(id, score)
                    ) glob ON l.id = glob.id
                    WHERE {filterWhere}
                ),
                tier_summary AS (
                    SELECT
                        COALESCE(bool_or(interest > 0), false) AS has_personal,
                        COALESCE(bool_or(popularity > 0), false) AS has_global,
                        COALESCE(max(interest), 0)::double precision AS max_interest,
                        COALESCE(max(popularity), 0)::double precision AS max_popularity
                    FROM valid_listings
                ),
                scored_listings AS (
                    SELECT
                        v.id,
                        v.created_at,
                        t.has_personal,
                        t.has_global,
                        CASE
                            WHEN t.has_personal THEN 'personalized'
                            WHEN t.has_global THEN 'global'
                            ELSE 'default'
                        END AS effective_tier,
                        CASE
                            WHEN t.has_personal THEN
                                1.0 + 12.0 * (CASE WHEN t.max_interest > 0 THEN v.interest::double precision / t.max_interest ELSE 0.0 END)
                                    + 4.0 * (CASE WHEN t.max_popularity > 0 THEN v.popularity::double precision / t.max_popularity ELSE 0.0 END)
                            WHEN t.has_global THEN
                                1.0 + 8.0 * (CASE WHEN t.max_popularity > 0 THEN v.popularity::double precision / t.max_popularity ELSE 0.0 END)
                            ELSE 1.0
                        END AS weight,
                        (('x' || substr(md5(@pSeed || ':' || v.id::text), 1, 8))::bit(32)::bigint + 1.0) / 4294967297.0 AS u
                    FROM valid_listings v
                    CROSS JOIN tier_summary t
                ),
                ordered_page AS (
                    SELECT
                        s.id,
                        s.effective_tier,
                        row_number() OVER (
                            ORDER BY
                                CASE
                                    WHEN s.has_personal OR s.has_global THEN (-ln(s.u) / s.weight)
                                END ASC NULLS LAST,
                                CASE
                                    WHEN NOT (s.has_personal OR s.has_global) THEN s.created_at
                                END DESC NULLS LAST,
                                CASE
                                    WHEN s.has_personal OR s.has_global THEN s.id
                                END ASC NULLS LAST,
                                CASE
                                    WHEN NOT (s.has_personal OR s.has_global) THEN s.id
                                END DESC NULLS LAST
                        ) AS page_ord
                    FROM scored_listings s
                    ORDER BY page_ord ASC
                    LIMIT @pTake OFFSET @pSkip
                )
                SELECT
                    op.id AS "Id",
                    ({totalQtyExpr}) AS "TotalQuantity",
                    ({availableQtyExpr}) AS "AvailableQuantity",
                    op.effective_tier AS "EffectiveTier"
                FROM ordered_page op
                JOIN "inventory"."equipment_listings" l ON l.id = op.id
                ORDER BY op.page_ord ASC
                """;
        }
        else
        {
            string orderSql;
            // Ưu tiên khớp toàn bộ name/model, sau đó khớp cụm từ; tiếp theo mới dùng thứ tự chỉ mục.
            if (criteria.Filters.Sort == AssetSortOption.Relevance)
            {
                bool hasKeyword = !string.IsNullOrWhiteSpace(criteria.Filters.SearchKeyword);
                if (hasKeyword)
                {
                    string kw = criteria.Filters.SearchKeyword!.Trim();
                    string exact = kw.ToLowerInvariant();
                    string phrase = $"%{EscapeLikePattern(kw)}%";
                    pageParamsList.Add(new NpgsqlParameter("pExact", exact));
                    pageParamsList.Add(new NpgsqlParameter("pPhrase", phrase));

                    if (hasCandidates)
                    {
                        orderSql = """
                            ORDER BY
                                CASE
                                    WHEN LOWER(l.name) = @pExact OR LOWER(l.model) = @pExact THEN 1
                                    WHEN l.name ILIKE @pPhrase ESCAPE '\' OR l.model ILIKE @pPhrase ESCAPE '\' THEN 2
                                    ELSE 3
                                END ASC,
                                cand.ord ASC,
                                l.id ASC
                            """;
                    }
                    else
                    {
                        orderSql = """
                            ORDER BY
                                CASE
                                    WHEN LOWER(l.name) = @pExact OR LOWER(l.model) = @pExact THEN 1
                                    WHEN l.name ILIKE @pPhrase ESCAPE '\' OR l.model ILIKE @pPhrase ESCAPE '\' THEN 2
                                    ELSE 3
                                END ASC,
                                l.created_at DESC,
                                l.id DESC
                            """;
                    }
                }
                else
                {
                    orderSql = hasCandidates
                        ? "ORDER BY cand.ord ASC, l.id ASC"
                        : "ORDER BY l.created_at DESC, l.id DESC";
                }
            }
            else
            {
                orderSql = criteria.Filters.Sort switch
                {
                    AssetSortOption.CreatedAsc => "ORDER BY l.created_at ASC, l.id ASC",
                    AssetSortOption.ModelDesc => "ORDER BY l.model DESC, l.id DESC",
                    AssetSortOption.ModelAsc => "ORDER BY l.model ASC, l.id ASC",
                    _ => "ORDER BY l.created_at DESC, l.id DESC"
                };
            }

            pageSql = $"""
                SELECT l.id AS "Id", ({totalQtyExpr}) AS "TotalQuantity", ({availableQtyExpr}) AS "AvailableQuantity", NULL AS "EffectiveTier"
                FROM "inventory"."equipment_listings" l
                {joinSql}
                {warehouseJoin}
                WHERE {filterWhere}
                {orderSql}
                LIMIT @pTake OFFSET @pSkip
                """;
        }

        string? holdExpirySql = null;
        NpgsqlParameter[]? holdParams = null;
        if (hasInterval)
        {
            holdExpirySql = $"""
                SELECT MIN(al.hold_expires_at) AS "Value"
                FROM "inventory"."equipment_listings" l
                {joinSql}
                {warehouseJoin}
                JOIN "inventory"."assets" a ON a.equipment_listing_id = l.id
                JOIN "inventory"."asset_allocations" al ON al.asset_id = a.id
                WHERE {baseWhere}
                  AND a.accepting_bookings = TRUE
                  AND al.status = @pHeldStatus
                  AND al.hold_expires_at > @pEvaluationUtc
                """;
            holdParams = CloneParameters(commonParams);
        }

        var countParams = CloneParameters(commonParams);
        var pageParams = CloneParameters(pageParamsList);

        return (countSql, pageSql, holdExpirySql, countParams, pageParams, holdParams);
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
        return parameters.Select(p => (NpgsqlParameter)((ICloneable)p).Clone()).ToArray();
    }
}
