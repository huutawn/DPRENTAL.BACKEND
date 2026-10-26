namespace Dprental.AnalyticsService.Application.Dashboard;

public sealed record OverviewResponse(
    DateOnly From, DateOnly To, string Currency,
    OrderMetrics Orders, FinancialMetrics Financial, RentalMetrics Rental);

public sealed record OrderMetrics(
    int Created, int Paid, int Active, int Completed, int Cancelled, int Expired);

public sealed record FinancialMetrics(
    decimal GrossRentalValue, decimal CompletedRentalValue, decimal DepositCollected,
    decimal PlatformRevenue, decimal DeductionAmount, decimal RefundAmount,
    decimal SupplierPayable, decimal PayoutCompleted);

public sealed record RentalMetrics(int PaidItems, int RentedQuantity, int CompletedRentals);

public sealed record OrderTrendPoint(DateOnly Date, int Created, int Paid, int Completed, int Cancelled, int Expired);
public sealed record OrdersResponse(DateOnly From, DateOnly To, int Active, IReadOnlyList<OrderTrendPoint> Points);

public sealed record EquipmentRank(Guid? Id, string Name, int RentalItemCount,
    int RentedQuantity, decimal GrossRentalValue);
public sealed record EquipmentResponse(DateOnly From, DateOnly To,
    IReadOnlyList<EquipmentRank> TopEquipmentTypes,
    IReadOnlyList<EquipmentRank> TopListings,
    IReadOnlyList<EquipmentRank> TopBrands);

public sealed record SupplierMetrics(Guid SupplierId, int Accepted, int Rejected,
    int Timeout, int CompletedRentals, decimal RentalValue,
    decimal SupplierPayable, decimal PayoutCompleted);
public sealed record SuppliersResponse(DateOnly From, DateOnly To, int Page, int PageSize,
    int Total, IReadOnlyList<SupplierMetrics> Items);
