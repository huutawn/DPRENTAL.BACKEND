using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.CatalogService.Domain.Repositories;

/// <summary>
/// Row mapping cho kết quả phân trang chứa ID và số đếm tin đăng từ quan hệ unnest PostgreSQL.
/// </summary>
internal sealed class PagedListingCountRow
{
    public Guid Id { get; set; }
    public int ListingCount { get; set; }
}

/// <summary>
/// Helper dùng chung để chuẩn bị mảng tham số cho quan hệ số đếm tạm thời unnest(@ids, @counts).
/// </summary>
internal static class CountRelationHelper
{
    private static (Guid[] Ids, int[] Counts) PrepareCountArrays(IReadOnlyDictionary<Guid, int> counts)
    {
        if (counts.Count == 0)
        {
            return (Array.Empty<Guid>(), Array.Empty<int>());
        }

        var ids = new Guid[counts.Count];
        var values = new int[counts.Count];
        int i = 0;
        foreach (var kvp in counts)
        {
            ids[i] = kvp.Key;
            values[i] = kvp.Value;
            i++;
        }

        return (ids, values);
    }

    public static IQueryable<PagedListingCountRow> Query(CatalogServiceDbContext context, IReadOnlyDictionary<Guid, int> counts)
    {
        var (ids, values) = PrepareCountArrays(counts);
        return context.Database.SqlQueryRaw<PagedListingCountRow>(
            "SELECT id AS \"Id\", listing_count AS \"ListingCount\" FROM unnest(@ids::uuid[], @counts::int[]) AS c(id, listing_count)",
            new NpgsqlParameter("ids", ids), new NpgsqlParameter("counts", values));
    }

    public static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
