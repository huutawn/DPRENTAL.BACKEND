namespace Dprental.AnalyticsService.Domain.IRepositories;

public sealed record FinancialFactMetrics(
    decimal GrossRentalValue,
    decimal DepositCollected,
    int PaidOrders,
    decimal PlatformRevenue,
    decimal FinalDeductionAmount,
    decimal SupplierPayable,
    decimal RefundAmount,
    decimal PayoutCompleted,
    decimal? PendingRefund);

public sealed record OrderFactCounts(
    int TotalOrders,
    int ActiveOrders,
    int CompletedOrders,
    int CancelledOrders,
    int ExpiredOrders,
    int RejectedItems);

public sealed record RentalFactMetrics(
    int PaidItems,
    int RentedQuantity,
    int CompletedRentals,
    decimal CompletedRentalValue);

public sealed record EquipmentRankRow(
    Guid? Id,
    string Name,
    int RentalItemCount,
    int RentedQuantity,
    decimal GrossRentalValue,
    int CompletedCount);

public sealed record EquipmentRankingsResult(
    IReadOnlyList<EquipmentRankRow> TopTypes,
    IReadOnlyList<EquipmentRankRow> TopListings,
    IReadOnlyList<EquipmentRankRow> TopBrands);

public sealed record SupplierRentalStatRow(
    Guid SupplierId,
    int Accepted,
    int Rejected,
    int Timeout,
    int Completed,
    decimal RentalValue);

public sealed record SupplierFinancialStatRow(
    Guid SupplierId,
    decimal Payable,
    decimal Paid);
