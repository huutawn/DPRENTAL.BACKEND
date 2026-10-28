using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Dashboard;

public sealed class OrderTrendQueryService(
    IRentalFactRepository rentalRepo,
    DashboardQueryService dashboard)
{
    public async Task<OrdersResponse> GetAsync(DashboardDateRange range, CancellationToken ct)
    {
        var pointsMap = await rentalRepo.GetOrderTrendPointsAsync(range.StartUtc, range.EndUtc, ct);

        var points = new List<OrderTrendPoint>();
        for (DateOnly day = range.From; day <= range.To; day = day.AddDays(1))
        {
            pointsMap.TryGetValue(day, out var counts);
            counts ??= new int[5];
            points.Add(new OrderTrendPoint(day, counts[0], counts[1], counts[2], counts[3], counts[4]));
        }

        int active = await dashboard.ActiveCountAsync(range, ct);
        return new OrdersResponse(range.From, range.To, active, points);
    }
}
