using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Một trang kết quả truy vấn kèm tổng số bản ghi khớp điều kiện.
/// </summary>
public sealed record RecordPage<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>
/// Projection một đơn hàng trong danh sách đơn của người thuê.
/// </summary>
public sealed record RenterOrderSummaryRecord(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    int ItemCount,
    int SubOrderCount,
    DateTimeOffset PaymentDeadline,
    DateTimeOffset CreatedAt);

/// <summary>
/// Projection một đơn con trong danh sách đơn của nhà cung cấp; chỉ gồm các dòng của chính đơn con đó.
/// </summary>
public sealed record SupplierSubOrderSummaryRecord(
    Guid SubOrderId,
    string SubOrderCode,
    string OrderCode,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    IReadOnlyList<RentalItemStatus> ItemStatuses,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt);
