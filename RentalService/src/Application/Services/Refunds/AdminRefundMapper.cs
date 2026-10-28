using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Refunds;

/// <summary>
/// Đổi khoản hoàn sang response của Admin. Tài khoản hiện đủ số: tài khoản đã chuyển khi đã xác nhận,
/// còn không là tài khoản người thuê khai lúc đặt hàng.
/// </summary>
internal static class AdminRefundMapper
{
    public static AdminOrderRefundResponse ToOrderRefund(RentalOrder order, RentalRefundLine line)
    {
        AdminRefundConfirmationResponse? confirmation = line.Confirmation is { } c
            ? ToConfirmation(c.RefundAmount, c.BankCode, c.AccountNumber, c.AccountHolderName, c.BankTransactionRef, c.Note, c.ConfirmedBy, c.ConfirmedAt)
            : null;

        return new AdminOrderRefundResponse(
            line.Item.Id,
            line.Item.ListingTitleSnapshot,
            line.Item.SupplierId,
            line.Item.Status,
            line.Kind,
            line.Item.RentalAmount,
            line.Item.DepositAmount,
            line.DeductionAmount,
            line.RefundAmount,
            confirmation?.RefundAccount ?? OrderQueryMapper.ParseRefundAccount(order.RefundDestinationRef),
            confirmation is null ? AdminRefundQueueState.Pending : AdminRefundQueueState.Completed,
            line.Request.Status,
            line.Request.CreatedAt,
            confirmation);
    }

    public static AdminRefundConfirmationResponse ToConfirmation(RefundConfirmationRecord c) =>
        ToConfirmation(c.RefundAmount, c.BankCode, c.AccountNumber, c.AccountHolderName, c.BankTransactionRef, c.Note, c.ConfirmedBy, c.ConfirmedAt);

    private static AdminRefundConfirmationResponse ToConfirmation(
        decimal refundAmount,
        string? bankCode,
        string? accountNumber,
        string? accountHolderName,
        string bankTransactionRef,
        string? note,
        Guid confirmedBy,
        DateTimeOffset confirmedAt) => new(
        refundAmount,
        string.IsNullOrWhiteSpace(bankCode) || string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(accountHolderName)
            ? null
            : new RenterRefundAccountResponse(bankCode, accountNumber, accountHolderName),
        bankTransactionRef,
        note,
        confirmedBy,
        confirmedAt);
}
