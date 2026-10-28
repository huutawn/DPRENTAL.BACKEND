using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Refunds;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Refunds;

/// <summary>
/// Admin chuyển khoản hoàn tiền cho người thuê rồi xác nhận ở Rental kèm mã giao dịch ngân hàng. Mỗi khoản hoàn chỉ
/// xác nhận một lần; gửi lại cùng mã giao dịch trả kết quả đã lưu. Khóa theo đơn để hai Admin không cùng xác nhận một khoản.
/// Số tiền và tài khoản nhận được chép vào xác nhận để đối soát sau này.
/// </summary>
public sealed partial class AdminRefundCommandService(
    IRentalOrderRepository orders,
    IRentalRefundRepository refunds,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AdminRefundCommandService> logger,
    IOrderHubNotifier? notifier = null) : IAdminRefundCommandService
{
    public const int MaxBankTransactionRefLength = 64;
    public const int MaxNoteLength = 500;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public async Task<Result<AdminOrderRefundResponse>> ConfirmTransferAsync(
        Guid rentalItemId,
        ConfirmRefundTransferRequest? request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result access = RentalAuthorizationPolicy.AuthorizeAdminRefundConfirm(caller);
        if (access.IsFailure)
        {
            return access.Error;
        }

        string bankTransactionRef = request?.BankTransactionRef?.Trim() ?? string.Empty;
        string? note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim();
        if (bankTransactionRef.Length == 0)
        {
            return RentalRefundErrors.InvalidRequest("Thiếu mã giao dịch ngân hàng.");
        }

        if (bankTransactionRef.Length > MaxBankTransactionRefLength)
        {
            return RentalRefundErrors.InvalidRequest($"Mã giao dịch ngân hàng tối đa {MaxBankTransactionRefLength} ký tự.");
        }

        if (note?.Length > MaxNoteLength)
        {
            return RentalRefundErrors.InvalidRequest($"Ghi chú tối đa {MaxNoteLength} ký tự.");
        }

        Guid? orderId = await orders.GetOrderIdByItemIdAsync(rentalItemId, cancellationToken);
        if (!orderId.HasValue)
        {
            return RentalRefundErrors.NotFound;
        }

        IAsyncDisposable orderLock;
        try
        {
            orderLock = await locks.AcquireOrderLockAsync(orderId.Value, LockTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return RentalRefundErrors.Busy;
        }

        RentalOrder order;
        RentalRefundLine confirmed;
        await using (orderLock)
        {
            RentalOrder? loaded = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
            RentalRefundLine? line = loaded is null
                ? null
                : RentalRefundPolicy.LinesOf(loaded, loaded.Items).FirstOrDefault(l => l.Item.Id == rentalItemId);
            if (loaded is null || line is null)
            {
                return RentalRefundErrors.NotFound;
            }

            if (line.Confirmation is { } existing)
            {
                return string.Equals(existing.BankTransactionRef, bankTransactionRef, StringComparison.Ordinal)
                    ? AdminRefundMapper.ToOrderRefund(loaded, line)
                    : RentalRefundErrors.AlreadyConfirmed;
            }

            if (await refunds.IsBankTransactionRefUsedAsync(bankTransactionRef, cancellationToken))
            {
                return RentalRefundErrors.DuplicateBankTransactionRef;
            }

            Guid adminId = caller.UserId!.Value;
            DateTimeOffset utcNow = timeProvider.GetUtcNow();
            var account = OrderQueryMapper.ParseRefundAccount(loaded.RefundDestinationRef);
            RentalRefundConfirmation confirmation = new(
                Guid.NewGuid(),
                loaded.Id,
                rentalItemId,
                line.Kind,
                line.RefundAmount,
                account?.BankCode,
                account?.AccountNumber,
                account?.AccountHolderName,
                bankTransactionRef,
                note,
                adminId,
                utcNow);
            loaded.AddRefundConfirmation(confirmation);
            loaded.RecordReadModelUpdate(utcNow);

            await orders.UpdateAsync(loaded, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            LogRefundConfirmed(logger, adminId, line.RefundAmount, rentalItemId, loaded.Id);
            order = loaded;
            confirmed = line with { Confirmation = confirmation };
        }

        if (notifier is not null)
        {
            await notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    OrderChangeTypes.RefundChanged,
                    confirmed.Confirmation!.ConfirmedAt),
                cancellationToken);
        }

        return AdminRefundMapper.ToOrderRefund(order, confirmed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} confirmed refund transfer of {Amount} for rental item {RentalItemId} in order {OrderId}")]
    private static partial void LogRefundConfirmed(ILogger logger, Guid adminId, decimal amount, Guid rentalItemId, Guid orderId);
}
