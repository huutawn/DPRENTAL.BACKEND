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

/// <summary>
/// Điều kiện tìm đơn của Admin; ô rỗng là không lọc. Các điều kiện cấp dòng (dòng, thiết bị, máy, nhà cung cấp,
/// trạng thái dòng) phải cùng khớp trên một dòng.
/// </summary>
public sealed record AdminOrderSearchCriteria(
    string? OrderCode,
    string? SubOrderCode,
    Guid? RentalItemId,
    Guid? ListingId,
    Guid? AssetId,
    Guid? SupplierId,
    Guid? RenterId,
    RentalOrderStatus? Status,
    RentalItemStatus? ItemStatus,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo);

/// <summary>
/// Projection một đơn trong kết quả tìm kiếm của Admin.
/// </summary>
public sealed record AdminOrderSummaryRecord(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    Guid RenterId,
    IReadOnlyList<Guid> SupplierIds,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    int ItemCount,
    string? InvoiceRef,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt);
