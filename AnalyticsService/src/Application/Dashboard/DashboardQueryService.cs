using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Dashboard;

public sealed class DashboardQueryService(
    IRentalFactRepository rentalRepo,
    IPaymentFinancialEventRepository paymentRepo)
{
    public async Task<OverviewResponse> OverviewAsync(
        DashboardDateRange range,
        Guid? supplierId = null,
        CancellationToken ct = default)
    {
        var orderCounts = await rentalRepo.GetOrderMetricsAsync(range.StartUtc, range.EndUtc, supplierId, ct);
        var financialCounts = await paymentRepo.GetFinancialMetricsAsync(range.StartUtc, range.EndUtc, supplierId, ct);
        var rentalCounts = await rentalRepo.GetRentalMetricsAsync(range.StartUtc, range.EndUtc, supplierId, ct);

        var orderMetrics = new OrderMetrics(
            orderCounts.TotalOrders,
            financialCounts.PaidOrders,
            orderCounts.ActiveOrders,
            orderCounts.CompletedOrders,
            orderCounts.CancelledOrders,
            orderCounts.ExpiredOrders,
            orderCounts.RejectedItems);

        var financial = new FinancialMetrics(
            financialCounts.GrossRentalValue,
            rentalCounts.CompletedRentalValue,
            financialCounts.DepositCollected,
            financialCounts.PlatformRevenue,
            financialCounts.FinalDeductionAmount,
            financialCounts.RefundAmount,
            financialCounts.SupplierPayable,
            financialCounts.PayoutCompleted,
            financialCounts.PendingRefund);

        var rental = new RentalMetrics(
            rentalCounts.PaidItems,
            rentalCounts.RentedQuantity,
            rentalCounts.CompletedRentals);

        return new OverviewResponse(range.From, range.To, "VND", orderMetrics, financial, rental);
    }

    public Task<int> ActiveCountAsync(DashboardDateRange range, CancellationToken ct) =>
        rentalRepo.GetActiveOrdersCountAsync(range.EndUtc, null, ct);

    public async Task<EquipmentResponse> EquipmentAsync(DashboardDateRange range, int limit, CancellationToken ct)
    {
        var ranks = await rentalRepo.GetEquipmentRankingsAsync(range.StartUtc, range.EndUtc, limit, ct);

        var types = ranks.TopTypes.Select(x => new EquipmentRank(
            x.Id, x.Name, x.RentalItemCount, x.RentedQuantity, x.GrossRentalValue, x.CompletedCount)).ToList();

        var listings = ranks.TopListings.Select(x => new EquipmentRank(
            x.Id, x.Name, x.RentalItemCount, x.RentedQuantity, x.GrossRentalValue, x.CompletedCount)).ToList();

        var brands = ranks.TopBrands.Select(x => new EquipmentRank(
            x.Id, x.Name, x.RentalItemCount, x.RentedQuantity, x.GrossRentalValue, x.CompletedCount)).ToList();

        return new EquipmentResponse(range.From, range.To, types, listings, brands);
    }

    public async Task<SuppliersResponse> SuppliersAsync(
        DashboardDateRange range, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Pagination must use page >= 1 and pageSize 1..100.");
        }

        var rentalCandidates = await rentalRepo.GetSupplierCandidatesAsync(range.StartUtc, range.EndUtc, ct);
        var paymentCandidates = await paymentRepo.GetSupplierCandidatesAsync(range.StartUtc, range.EndUtc, ct);
        var candidates = rentalCandidates.Union(paymentCandidates).OrderBy(x => x).ToList();

        int total = candidates.Count;
        Guid[] ids = candidates.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        if (ids.Length == 0)
        {
            return new SuppliersResponse(range.From, range.To, page, pageSize, total, []);
        }

        var requestCounts = await rentalRepo.GetSupplierRequestCountsAsync(ids, range.StartUtc, range.EndUtc, ct);
        var rentalStats = await rentalRepo.GetSupplierRentalStatsAsync(ids, range.StartUtc, range.EndUtc, ct);
        var financialStats = await paymentRepo.GetSupplierFinancialStatsAsync(ids, range.StartUtc, range.EndUtc, ct);

        var result = ids.Select(id =>
        {
            rentalStats.TryGetValue(id, out var rental);
            financialStats.TryGetValue(id, out var money);
            requestCounts.TryGetValue(id, out int requests);
            return new SupplierMetrics(
                id,
                requests,
                rental?.Accepted ?? 0,
                rental?.Rejected ?? 0,
                rental?.Timeout ?? 0,
                rental?.Completed ?? 0,
                rental?.RentalValue ?? 0,
                money?.Payable ?? 0,
                money?.Paid ?? 0);
        }).ToList();

        return new SuppliersResponse(range.From, range.To, page, pageSize, total, result);
    }
}
