using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Các khoản Rental đã yêu cầu hoàn cho người thuê và xác nhận chuyển khoản của Admin.
/// Một khoản là một yêu cầu gắn với một dòng thiết bị: <c>RequestRefund</c> (hoàn toàn bộ tiền thuê và cọc)
/// hoặc <c>SettleRental</c> (hoàn phần cọc còn lại sau khấu trừ; bỏ qua khi phần đó bằng 0). Không theo dõi thay đổi.
/// </summary>
public interface IRentalRefundRepository
{
    /// <summary>
    /// Một trang khoản hoàn. <paramref name="completed"/> false: chưa có xác nhận chuyển khoản, cũ nhất trước;
    /// true: đã xác nhận, mới nhất trước; null: tất cả, mới nhất trước.
    /// </summary>
    Task<RecordPage<RefundQueueRecord>> GetQueuePageAsync(
        bool? completed,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetReconciliationItemIdsAsync(Guid? after, int take,
        CancellationToken cancellationToken = default);

    /// <summary>Mã giao dịch ngân hàng đã được dùng cho một xác nhận chuyển khoản nào chưa.</summary>
    Task<bool> IsBankTransactionRefUsedAsync(string bankTransactionRef, CancellationToken cancellationToken = default);
}

/// <summary>
/// Projection một khoản hoàn kèm thông tin đơn, dòng thiết bị, tài khoản nhận hoàn tiền đã lưu lúc đặt hàng
/// và xác nhận chuyển khoản nếu có.
/// </summary>
public sealed record RefundQueueRecord(
    Guid RentalItemId,
    Guid RentalOrderId,
    string OrderCode,
    Guid RenterId,
    string RecipientName,
    string RecipientPhone,
    string RefundDestinationRef,
    string EquipmentName,
    Guid SupplierId,
    RentalItemStatus ItemStatus,
    string RequestAction,
    string RequestStatus,
    decimal RentalAmount,
    decimal DepositAmount,
    decimal? FinalDeductionAmount,
    DateTimeOffset RequestedAt,
    RefundConfirmationRecord? Confirmation);

/// <summary>
/// Xác nhận chuyển khoản hoàn tiền của Admin.
/// </summary>
public sealed record RefundConfirmationRecord(
    decimal RefundAmount,
    string? BankCode,
    string? AccountNumber,
    string? AccountHolderName,
    string BankTransactionRef,
    string? Note,
    Guid ConfirmedBy,
    DateTimeOffset ConfirmedAt);
