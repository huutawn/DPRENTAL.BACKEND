using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
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

/// <summary>Payment owns financial completion. Rental stores its verified confirmation for existing FE contracts.</summary>
public sealed partial class AdminRefundCommandService(
    IRentalOrderRepository orders,
    IRentalRefundRepository refunds,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AdminRefundCommandService> logger,
    IPaymentRentalClient payment,
    IOrderHubNotifier? notifier = null) : IAdminRefundCommandService
{
    public const int MaxBankTransactionRefLength = 64;
    public const int MaxNoteLength = 500;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public async Task<Result<AdminOrderRefundResponse>> ConfirmTransferAsync(
        Guid rentalItemId, ConfirmRefundTransferRequest? request, IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result access = RentalAuthorizationPolicy.AuthorizeAdminRefundConfirm(caller);
        if (access.IsFailure) return access.Error;
        string reference = request?.BankTransactionRef?.Trim() ?? string.Empty;
        string? note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim();
        if (reference.Length is 0 or > MaxBankTransactionRefLength || note?.Length > MaxNoteLength)
            return RentalRefundErrors.InvalidRequest("Mã giao dịch bắt buộc, tối đa 64 ký tự; ghi chú tối đa 500 ký tự.");
        var result = await SynchronizeAsync(rentalItemId, new(reference, note), caller.UserId, true, cancellationToken);
        if (result.IsFailure) return result.Error;
        return result.Value.Response is { } response
            ? response : Error.Conflict("Refund.AwaitingPayment", "Payment chưa xác nhận hoàn tiền.");
    }

    public async Task<Result<bool>> ReconcileAsync(Guid rentalItemId, bool apply,
        CancellationToken cancellationToken = default)
    {
        var result = await SynchronizeAsync(rentalItemId, null, null, apply, cancellationToken);
        return result.IsFailure ? result.Error : result.Value.Changed;
    }

    private async Task<Result<(AdminOrderRefundResponse? Response, bool Changed)>> SynchronizeAsync(
        Guid itemId, ConfirmRefundTransferRequest? request, Guid? adminId, bool apply, CancellationToken ct)
    {
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, ct);
        if (!orderId.HasValue) return RentalRefundErrors.NotFound;
        IAsyncDisposable orderLock;
        try { orderLock = await locks.AcquireOrderLockAsync(orderId.Value, LockTimeout, ct); }
        catch (TimeoutException) { return RentalRefundErrors.Busy; }

        RentalOrder order;
        RentalRefundLine line;
        bool changed;
        await using (orderLock)
        {
            var loaded = await orders.GetByIdWithDetailsAsync(orderId.Value, ct);
            var found = loaded is null ? null : RentalRefundPolicy.LinesOf(loaded, loaded.Items)
                .FirstOrDefault(x => x.Item.Id == itemId);
            if (loaded is null || found is null) return RentalRefundErrors.NotFound;
            order = loaded;
            line = found;
            var existing = line.Confirmation;
            if (request is not null && existing is not null && existing.BankTransactionRef != request.BankTransactionRef)
                return RentalRefundErrors.AlreadyConfirmed;
            if (existing is not null && existing.RefundAmount != line.RefundAmount)
                return Error.Conflict("Refund.EvidenceMismatch", "Số tiền xác nhận cũ khác khoản hoàn; cần đối soát.");
            string? reference = existing?.BankTransactionRef ?? request?.BankTransactionRef;
            if (existing is null && reference is not null && await refunds.IsBankTransactionRefUsedAsync(reference, ct))
                return RentalRefundErrors.DuplicateBankTransactionRef;

            // Only a session advisory lock is held here: no database transaction spans the HTTP call.
            // No local success is written until Payment's authoritative workflow completes.
            var synced = await payment.SynchronizeRefundAsync(new RentalRefundSyncRequest(
                order.Id, itemId, line.Request.OperationId, line.RefundAmount,
                apply ? reference : null,
                existing?.ConfirmedBy ?? adminId,
                existing?.ConfirmedAt ?? (adminId.HasValue ? timeProvider.GetUtcNow() : null),
                existing?.Note ?? request?.Note), ct);
            if (synced.IsFailure) return synced.Error;
            var evidence = synced.Value;
            changed = (existing is not null && !evidence.Completed) || (existing is null && evidence.Completed);
            if (!evidence.Completed) return ((AdminOrderRefundResponse?)null, !apply && changed);
            if (evidence.Amount != line.RefundAmount || string.IsNullOrWhiteSpace(evidence.BankTransactionRef) ||
                evidence.BankTransactionRef.Length > MaxBankTransactionRefLength ||
                evidence.ConfirmedBy is null || evidence.ConfirmedBy == Guid.Empty || evidence.ConfirmedAt is null ||
                evidence.ConfirmedAt == default(DateTimeOffset) || evidence.ConfirmedAt > timeProvider.GetUtcNow() ||
                (reference is not null && reference != evidence.BankTransactionRef) ||
                (existing is not null && existing.RefundAmount != evidence.Amount))
                return Error.Conflict("Refund.EvidenceMismatch", "Bằng chứng Payment không khớp khoản hoàn; cần đối soát.");
            if (!apply) return ((AdminOrderRefundResponse?)null, changed);
            changed = evidence.ConfirmationApplied;
            if (existing is null)
            {
                if (await refunds.IsBankTransactionRefUsedAsync(evidence.BankTransactionRef, ct))
                    return RentalRefundErrors.DuplicateBankTransactionRef;
                var account = OrderQueryMapper.ParseRefundAccount(order.RefundDestinationRef);
                var confirmation = new RentalRefundConfirmation(Guid.NewGuid(), order.Id, itemId, line.Kind,
                    evidence.Amount, account?.BankCode, account?.AccountNumber, account?.AccountHolderName,
                    evidence.BankTransactionRef, request?.Note, evidence.ConfirmedBy.Value, evidence.ConfirmedAt.Value);
                order.AddRefundConfirmation(confirmation);
                order.RecordReadModelUpdate(timeProvider.GetUtcNow());
                await orders.UpdateAsync(order, ct);
                await unitOfWork.SaveChangesAsync(ct);
                line = line with { Confirmation = confirmation };
                changed = true;
                LogRefundConfirmed(logger, evidence.ConfirmedBy.Value, evidence.Amount, itemId, order.Id);
            }
        }
        if (changed && notifier is not null)
            await notifier.NotifyRenterOrderChangedAsync(order.RenterId,
                new RenterOrderChangedNotification(Guid.NewGuid(), order.Id, order.Version,
                    OrderChangeTypes.RefundChanged, timeProvider.GetUtcNow()), ct);
        return (AdminRefundMapper.ToOrderRefund(order, line), changed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} confirmed refund transfer of {Amount} for rental item {RentalItemId} in order {OrderId}")]
    private static partial void LogRefundConfirmed(ILogger logger, Guid adminId, decimal amount, Guid rentalItemId, Guid orderId);
}
