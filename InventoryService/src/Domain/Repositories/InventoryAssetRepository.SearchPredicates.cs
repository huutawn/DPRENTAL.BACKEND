using System.Globalization;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.IRepositories;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

public sealed partial class InventoryAssetRepository
{
    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
    }

    private static string BuildOrderSql(AssetSortOption sort)
    {
        return sort switch
        {
            AssetSortOption.CreatedAsc => "ORDER BY a.created_at ASC, a.id ASC",
            AssetSortOption.ModelDesc => "ORDER BY a.model DESC, a.id DESC",
            AssetSortOption.ModelAsc => "ORDER BY a.model ASC, a.id ASC",
            AssetSortOption.ManufactureYearDesc => "ORDER BY a.manufacture_year DESC NULLS LAST, a.id DESC",
            AssetSortOption.ManufactureYearAsc => "ORDER BY a.manufacture_year ASC NULLS LAST, a.id ASC",
            _ => "ORDER BY a.created_at DESC, a.id DESC"
        };
    }

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildOwnerPredicates(
        OwnerAssetSearchCriteria criteria,
        bool includeDynamicFilters)
    {
        var clauses = new List<string>
        {
            "a.owner_user_id = @pOwner"
        };

        var parameters = new List<NpgsqlParameter>
        {
            new("pOwner", criteria.OwnerUserId)
        };

        if (criteria.AcceptingBookings.HasValue)
        {
            clauses.Add("a.accepting_bookings = @pAcceptingBookings");
            parameters.Add(new NpgsqlParameter("pAcceptingBookings", criteria.AcceptingBookings.Value));
        }

        var filters = criteria.Filters;
        if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var escaped = EscapeLikePattern(filters.SearchKeyword);
            clauses.Add("(a.model ILIKE @pSearch ESCAPE '\\' OR (a.serial_number IS NOT NULL AND a.serial_number ILIKE @pSearch ESCAPE '\\'))");
            parameters.Add(new NpgsqlParameter("pSearch", $"%{escaped}%"));
        }

        AppendCommonMetadataFilters(clauses, parameters, filters);

        if (includeDynamicFilters)
        {
            AppendDynamicFilters(clauses, parameters, filters);
        }

        string whereSql = string.Join(" AND ", clauses);
        return (whereSql, parameters);
    }

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildPublicPredicates(
        PublicAssetSearchCriteria criteria,
        bool includeDynamicFilters)
    {
        var clauses = new List<string>
        {
            "a.accepting_bookings = TRUE"
        };

        var parameters = new List<NpgsqlParameter>();

        var filters = criteria.Filters;
        if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var escaped = EscapeLikePattern(filters.SearchKeyword);
            clauses.Add("a.model ILIKE @pSearch ESCAPE '\\'");
            parameters.Add(new NpgsqlParameter("pSearch", $"%{escaped}%"));
        }

        AppendCommonMetadataFilters(clauses, parameters, filters);

        if (includeDynamicFilters)
        {
            AppendDynamicFilters(clauses, parameters, filters);
        }

        AppendAvailabilityPredicates(clauses, parameters, criteria);

        string whereSql = string.Join(" AND ", clauses);
        return (whereSql, parameters);
    }

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildPublicDistinctVersionPredicates(
        AssetSearchFilters filters)
    {
        var clauses = new List<string>
        {
            "a.accepting_bookings = TRUE"
        };

        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var escaped = EscapeLikePattern(filters.SearchKeyword);
            clauses.Add("a.model ILIKE @pSearch ESCAPE '\\'");
            parameters.Add(new NpgsqlParameter("pSearch", $"%{escaped}%"));
        }

        AppendCommonMetadataFilters(clauses, parameters, filters);

        string whereSql = string.Join(" AND ", clauses);
        return (whereSql, parameters);
    }

    private static void AppendCommonMetadataFilters(
        List<string> clauses,
        List<NpgsqlParameter> parameters,
        AssetSearchFilters filters)
    {
        if (filters.EquipmentTypeId.HasValue)
        {
            clauses.Add("a.equipment_type_id = @pEquipmentTypeId");
            parameters.Add(new NpgsqlParameter("pEquipmentTypeId", filters.EquipmentTypeId.Value));
        }

        if (filters.BrandId.HasValue)
        {
            clauses.Add("a.brand_id = @pBrandId");
            parameters.Add(new NpgsqlParameter("pBrandId", filters.BrandId.Value));
        }

        if (filters.ManufactureYearFrom.HasValue)
        {
            clauses.Add("a.manufacture_year IS NOT NULL AND a.manufacture_year >= @pYearFrom");
            parameters.Add(new NpgsqlParameter("pYearFrom", filters.ManufactureYearFrom.Value));
        }

        if (filters.ManufactureYearTo.HasValue)
        {
            clauses.Add("a.manufacture_year IS NOT NULL AND a.manufacture_year <= @pYearTo");
            parameters.Add(new NpgsqlParameter("pYearTo", filters.ManufactureYearTo.Value));
        }
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

        clauses.Add("a.schema_version = ANY(@pCompatibleVersions)");
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

                clauses.Add($"(CASE WHEN jsonb_typeof(a.spec_values -> @{keyParam}) = 'number' THEN (a.spec_values ->> @{keyParam})::numeric ELSE NULL END) {op} @{valParam}");
            }
            else if (string.Equals(filter.DataType, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                bool boolVal = Convert.ToBoolean(filter.Value, CultureInfo.InvariantCulture);
                parameters.Add(new NpgsqlParameter(valParam, boolVal));

                clauses.Add($"jsonb_typeof(a.spec_values -> @{keyParam}) = 'boolean' AND (a.spec_values -> @{keyParam}) = to_jsonb(@{valParam}::boolean)");
            }
            else
            {
                string strVal = filter.Value?.ToString() ?? string.Empty;
                parameters.Add(new NpgsqlParameter(valParam, strVal));

                clauses.Add($"jsonb_typeof(a.spec_values -> @{keyParam}) = 'string' AND (a.spec_values ->> @{keyParam}) = @{valParam}");
            }
        }
    }

    private static void AppendAvailabilityPredicates(
        List<string> clauses,
        List<NpgsqlParameter> parameters,
        PublicAssetSearchCriteria criteria)
    {
        bool hasFrom = criteria.AvailabilityFrom.HasValue;
        bool hasTo = criteria.AvailabilityTo.HasValue;
        if (!hasFrom && !hasTo)
        {
            return;
        }

        if (!hasFrom || !hasTo)
        {
            throw new ArgumentException("AvailabilityFrom và AvailabilityTo phải được cung cấp đồng thời.", nameof(criteria));
        }

        if (!criteria.AvailabilityEvaluationUtc.HasValue)
        {
            throw new ArgumentException("AvailabilityEvaluationUtc là bắt buộc khi có cửa sổ ngày khả dụng.", nameof(criteria));
        }

        parameters.Add(new NpgsqlParameter("pAvailabilityFrom", criteria.AvailabilityFrom!.Value));
        parameters.Add(new NpgsqlParameter("pAvailabilityTo", criteria.AvailabilityTo!.Value));

        clauses.Add("""
            NOT EXISTS (
                SELECT 1 FROM "inventory"."calendar_blocks" cb
                WHERE cb.asset_id = a.id
                  AND cb.start_date <= @pAvailabilityTo
                  AND cb.end_date >= @pAvailabilityFrom
            )
            """);

        parameters.Add(new NpgsqlParameter("pAllocatedStatus", (int)AllocationStatus.Allocated));
        parameters.Add(new NpgsqlParameter("pHeldStatus", (int)AllocationStatus.Held));
        parameters.Add(new NpgsqlParameter("pAvailabilityEvaluationUtc", criteria.AvailabilityEvaluationUtc.Value));

        clauses.Add("""
            NOT EXISTS (
                SELECT 1 FROM "inventory"."asset_allocations" al
                WHERE al.asset_id = a.id
                  AND al.start_date <= @pAvailabilityTo
                  AND al.end_date >= @pAvailabilityFrom
                  AND (
                      al.status = @pAllocatedStatus
                      OR (al.status = @pHeldStatus AND al.hold_expires_at > @pAvailabilityEvaluationUtc)
                  )
            )
            """);
    }

    private static NpgsqlParameter[] CloneParameters(IEnumerable<NpgsqlParameter> parameters)
    {
        return parameters.Select(p => new NpgsqlParameter(p.ParameterName, p.Value)
        {
            NpgsqlDbType = p.NpgsqlDbType,
            DataTypeName = p.DataTypeName
        }).ToArray();
    }
}
