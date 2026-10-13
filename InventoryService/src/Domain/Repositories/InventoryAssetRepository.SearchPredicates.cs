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


    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildOwnerPredicates(
        OwnerAssetSearchCriteria criteria,
        bool includeDynamicFilters)
    {
        var clauses = new List<string>
        {
            "l.owner_user_id = @pOwner"
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
        if (filters.CandidateIds != null)
        {
            if (filters.CandidateIds.Count == 0)
            {
                clauses.Add("1 = 0");
            }
        }
        else if (!string.IsNullOrWhiteSpace(filters.SearchKeyword))
        {
            var escaped = EscapeLikePattern(filters.SearchKeyword);
            clauses.Add("(l.model ILIKE @pSearch ESCAPE '\\' OR (a.serial_number IS NOT NULL AND a.serial_number ILIKE @pSearch ESCAPE '\\'))");
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

    private static void AppendCommonMetadataFilters(
        List<string> clauses,
        List<NpgsqlParameter> parameters,
        AssetSearchFilters filters)
    {
        if (filters.EquipmentTypeId.HasValue)
        {
            clauses.Add("l.equipment_type_id = @pEquipmentTypeId");
            parameters.Add(new NpgsqlParameter("pEquipmentTypeId", filters.EquipmentTypeId.Value));
        }

        if (filters.BrandId.HasValue)
        {
            clauses.Add("l.brand_id = @pBrandId");
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
}
