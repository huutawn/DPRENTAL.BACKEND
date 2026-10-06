using System.Data;
using System.Globalization;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository truy xuất và lưu trữ thực thể InventoryAsset trên PostgreSQL adapter.
/// </summary>
public sealed class InventoryAssetRepository : IInventoryAssetRepository
{
    private readonly InventoryServiceDbContext _context;

    public InventoryAssetRepository(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .FirstOrDefaultAsync(a => a.Id == id && a.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAsset>> SearchAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(criteria));
        }

        var (whereSql, parameters) = BuildPredicates(criteria, includeDynamicFilters: true);

        string orderSql = criteria.Sort switch
        {
            AssetSortOption.CreatedAsc => "ORDER BY created_at ASC, id ASC",
            AssetSortOption.ModelDesc => "ORDER BY model DESC, id DESC",
            AssetSortOption.ModelAsc => "ORDER BY model ASC, id ASC",
            AssetSortOption.ManufactureYearDesc => "ORDER BY manufacture_year DESC NULLS LAST, id DESC",
            AssetSortOption.ManufactureYearAsc => "ORDER BY manufacture_year ASC NULLS LAST, id ASC",
            _ => "ORDER BY created_at DESC, id DESC"
        };

        parameters.Add(new NpgsqlParameter("pTake", criteria.Take));
        parameters.Add(new NpgsqlParameter("pSkip", criteria.Skip));

        string sql = $"""
            SELECT *, xmin FROM "inventory"."assets"
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
    public async Task<int> CountAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(criteria));
        }

        var (whereSql, parameters) = BuildPredicates(criteria, includeDynamicFilters: true);

        string sql = $"""
            SELECT COUNT(*)::int AS "Value" FROM "inventory"."assets"
            WHERE {whereSql}
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .SingleAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetDistinctSchemaVersionsAsync(AssetSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(criteria));
        }

        var (whereSql, parameters) = BuildPredicates(criteria, includeDynamicFilters: false);

        string sql = $"""
            SELECT DISTINCT schema_version AS "Value" FROM "inventory"."assets"
            WHERE {whereSql}
            ORDER BY "Value" ASC
            """;

        return await _context.Database
            .SqlQueryRaw<int>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);
    }

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildPredicates(
        AssetSearchCriteria criteria,
        bool includeDynamicFilters)
    {
        var clauses = new List<string>
        {
            "owner_user_id = @pOwner"
        };

        var parameters = new List<NpgsqlParameter>
        {
            new("pOwner", criteria.OwnerUserId)
        };

        if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
        {
            var escaped = criteria.SearchKeyword
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            clauses.Add("(model ILIKE @pSearch ESCAPE '\\' OR (serial_number IS NOT NULL AND serial_number ILIKE @pSearch ESCAPE '\\'))");
            parameters.Add(new NpgsqlParameter("pSearch", $"%{escaped}%"));
        }

        if (criteria.EquipmentTypeId.HasValue)
        {
            clauses.Add("equipment_type_id = @pEquipmentTypeId");
            parameters.Add(new NpgsqlParameter("pEquipmentTypeId", criteria.EquipmentTypeId.Value));
        }

        if (criteria.BrandId.HasValue)
        {
            clauses.Add("brand_id = @pBrandId");
            parameters.Add(new NpgsqlParameter("pBrandId", criteria.BrandId.Value));
        }

        if (criteria.AcceptingBookings.HasValue)
        {
            clauses.Add("accepting_bookings = @pAcceptingBookings");
            parameters.Add(new NpgsqlParameter("pAcceptingBookings", criteria.AcceptingBookings.Value));
        }

        if (criteria.ManufactureYearFrom.HasValue)
        {
            clauses.Add("manufacture_year IS NOT NULL AND manufacture_year >= @pYearFrom");
            parameters.Add(new NpgsqlParameter("pYearFrom", criteria.ManufactureYearFrom.Value));
        }

        if (criteria.ManufactureYearTo.HasValue)
        {
            clauses.Add("manufacture_year IS NOT NULL AND manufacture_year <= @pYearTo");
            parameters.Add(new NpgsqlParameter("pYearTo", criteria.ManufactureYearTo.Value));
        }

        if (includeDynamicFilters && criteria.DynamicFilters != null && criteria.DynamicFilters.Count > 0)
        {
            if (criteria.CompatibleSchemaVersions == null || criteria.CompatibleSchemaVersions.Count == 0)
            {
                clauses.Add("1 = 0");
            }
            else
            {
                clauses.Add("schema_version = ANY(@pCompatibleVersions)");
                parameters.Add(new NpgsqlParameter("pCompatibleVersions", criteria.CompatibleSchemaVersions.ToArray()));

                for (int i = 0; i < criteria.DynamicFilters.Count; i++)
                {
                    var filter = criteria.DynamicFilters[i];
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

                        clauses.Add($"(CASE WHEN jsonb_typeof(spec_values -> @{keyParam}) = 'number' THEN (spec_values ->> @{keyParam})::numeric ELSE NULL END) {op} @{valParam}");
                    }
                    else if (string.Equals(filter.DataType, "boolean", StringComparison.OrdinalIgnoreCase))
                    {
                        bool boolVal = Convert.ToBoolean(filter.Value, CultureInfo.InvariantCulture);
                        parameters.Add(new NpgsqlParameter(valParam, boolVal));

                        clauses.Add($"jsonb_typeof(spec_values -> @{keyParam}) = 'boolean' AND (spec_values -> @{keyParam}) = to_jsonb(@{valParam}::boolean)");
                    }
                    else
                    {
                        string strVal = filter.Value?.ToString() ?? string.Empty;
                        parameters.Add(new NpgsqlParameter(valParam, strVal));

                        clauses.Add($"jsonb_typeof(spec_values -> @{keyParam}) = 'string' AND (spec_values ->> @{keyParam}) = @{valParam}");
                    }
                }
            }
        }

        string whereSql = string.Join(" AND ", clauses);
        return (whereSql, parameters);
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetCalendarSnapshotAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .AsNoTrackingWithIdentityResolution()
            .AsSingleQuery()
            .Include(a => a.CalendarBlocks.Where(cb => cb.StartDate <= endDate && cb.EndDate >= startDate))
            .Include(a => a.Allocations.Where(al =>
                (al.Status == AllocationStatus.Held || al.Status == AllocationStatus.Allocated) &&
                al.RentalPeriod.StartDate <= endDate &&
                al.RentalPeriod.EndDate >= startDate))
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(
        IReadOnlyList<Guid> assetIds,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (assetIds == null || assetIds.Count == 0)
        {
            return Array.Empty<InventoryAsset>();
        }

        var query = _context.Assets
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Where(a => assetIds.Contains(a.Id))
            .Include(a => a.CalendarBlocks.Where(cb => cb.StartDate <= endDate && cb.EndDate >= startDate))
            .Include(a => a.Allocations.Where(al =>
                (al.Status == AllocationStatus.Held || al.Status == AllocationStatus.Allocated) &&
                al.RentalPeriod.StartDate <= endDate &&
                al.RentalPeriod.EndDate >= startDate));

        if (_context.Database.IsRelational())
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var results = await query.ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return results;
        }

        return await query.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _context.Assets.Add(asset);
    }

    /// <inheritdoc />
    public void Update(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _context.Assets.Update(asset);
    }
}
