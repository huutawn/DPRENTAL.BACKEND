using System.Data;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Application.Dashboard;

public sealed class OrderTrendQueryService(AnalyticsServiceDbContext db, DashboardQueryService dashboard)
{
    public async Task<OrdersResponse> GetAsync(DashboardDateRange range, CancellationToken ct)
    {
        var points = new Dictionary<DateOnly, int[]>();
        for (DateOnly day = range.From; day <= range.To; day = day.AddDays(1))
        {
            points[day] = new int[5];
        }

        var connection = db.Database.GetDbConnection();
        bool opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT category, (moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day, COUNT(*)::integer
                FROM (
                  SELECT 0 AS category, "CreatedAt" AS moment FROM analytics_rental_orders
                  UNION ALL SELECT 1, "PaidAt" FROM analytics_rental_orders
                  UNION ALL SELECT 2, "CompletedAt" FROM analytics_rental_orders
                  UNION ALL SELECT 3, "CancelledAt" FROM analytics_rental_orders
                  UNION ALL SELECT 4, "ExpiredAt" FROM analytics_rental_orders
                ) t
                WHERE moment >= @start AND moment < @end
                GROUP BY category, day;
                """;
            Add(command, "start", range.StartUtc);
            Add(command, "end", range.EndUtc);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                DateOnly date = reader.GetFieldValue<DateOnly>(1);
                points[date][reader.GetInt32(0)] = reader.GetInt32(2);
            }
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }

        return new OrdersResponse(range.From, range.To, await dashboard.ActiveCountAsync(range, ct),
            points.Select(x => new OrderTrendPoint(x.Key, x.Value[0], x.Value[1], x.Value[2],
                x.Value[3], x.Value[4])).ToList());
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
