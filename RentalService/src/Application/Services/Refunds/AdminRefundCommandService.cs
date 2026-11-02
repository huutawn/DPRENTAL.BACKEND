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

    // Kiểm tra quyền admin và chứng từ nhập, rồi đối soát với Payment trước khi trả khoản hoàn đã xác nhận.
    public async Task<Result<AdminOrderRefundResponse>> ConfirmTransferAsync(
        Guid rentalItemId, ConfirmRefundTransferRequest? request, IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result access = RentalAuthorizationPolicy.AuthorizeAdminRefundConfirm(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (access.IsFailure) return access.Error;
        string reference = request?.BankTransactionRef?.Trim() ?? string.Empty;
        string? note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim();
        // Đối chiếu tham chiếu với dữ liệu đã lưu để không dùng lại chứng từ hoặc ghi nhận kết quả không cùng thao tác.
        if (reference.Length is 0 or > MaxBankTransactionRefLength || note?.Length > MaxNoteLength)
            return RentalRefundErrors.InvalidRequest("Mã giao dịch bắt buộc, tối đa 64 ký tự; ghi chú tối đa 500 ký tự.");
        var result = await SynchronizeAsync(rentalItemId, new(reference, note), caller.UserId, true, cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (result.IsFailure) return result.Error;
        return result.Value.Response is { } response
            ? response : Error.Conflict("Refund.AwaitingPayment", "Payment chưa xác nhận hoàn tiền.");
    }

    // Đối soát khoản hoàn theo item; apply=false chỉ kiểm tra chênh lệch, apply=true mới đồng bộ xác nhận.
    public async Task<Result<bool>> ReconcileAsync(Guid rentalItemId, bool apply,
        CancellationToken cancellationToken = default)
    {
        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        var result = await SynchronizeAsync(rentalItemId, null, null, apply, cancellationToken);
        return result.IsFailure ? result.Error : result.Value.Changed;
    }

    // Khóa order, chống dùng lại mã giao dịch, đối chiếu bằng chứng Payment và chỉ lưu xác nhận khi số tiền/định danh/thời gian khớp.
    private async Task<Result<(AdminOrderRefundResponse? Response, bool Changed)>> SynchronizeAsync(
        Guid itemId, ConfirmRefundTransferRequest? request, Guid? adminId, bool apply, CancellationToken ct)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, ct);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalRefundErrors.NotFound;
        IAsyncDisposable orderLock;
        // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
        try { orderLock = await locks.AcquireOrderLockAsync(orderId.Value, LockTimeout, ct); }
        // Không lấy được khóa trong thời gian cho phép: trả trạng thái bận/chưa sẵn sàng để caller thử lại thay vì ghi đồng thời.
        catch (TimeoutException) { return RentalRefundErrors.Busy; }

        // Giữ aggregate order dùng xuyên suốt phạm vi xử lý; dữ liệu phải được đọc theo repository/khóa trước khi cập nhật.
        RentalOrder order;
        RentalRefundLine line;
        bool changed;
        await using (orderLock)
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var loaded = await orders.GetByIdWithDetailsAsync(orderId.Value, ct);
            var found = loaded is null ? null : RentalRefundPolicy.LinesOf(loaded, loaded.Items)
                .FirstOrDefault(x => x.Item.Id == itemId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (loaded is null || found is null) return RentalRefundErrors.NotFound;
            order = loaded;
            line = found;
            var existing = line.Confirmation;
            // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
            if (request is not null && existing is not null && existing.BankTransactionRef != request.BankTransactionRef)
                return RentalRefundErrors.AlreadyConfirmed;
            // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
            if (existing is not null && existing.RefundAmount != line.RefundAmount)
                return Error.Conflict("Refund.EvidenceMismatch", "Số tiền xác nhận cũ khác khoản hoàn; cần đối soát.");
            string? reference = existing?.BankTransactionRef ?? request?.BankTransactionRef;
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
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
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (synced.IsFailure) return synced.Error;
            var evidence = synced.Value;
            changed = (existing is not null && !evidence.Completed) || (existing is null && evidence.Completed);
            // Chưa có bằng chứng Payment hoàn tất thì không tạo xác nhận chuyển tiền ở Rental; nhánh dry-run chỉ báo chênh lệch.
            if (!evidence.Completed) return ((AdminOrderRefundResponse?)null, !apply && changed);
            // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
            if (evidence.Amount != line.RefundAmount || string.IsNullOrWhiteSpace(evidence.BankTransactionRef) ||
                evidence.BankTransactionRef.Length > MaxBankTransactionRefLength ||
                evidence.ConfirmedBy is null || evidence.ConfirmedBy == Guid.Empty || evidence.ConfirmedAt is null ||
                evidence.ConfirmedAt == default(DateTimeOffset) || evidence.ConfirmedAt > timeProvider.GetUtcNow() ||
                (reference is not null && reference != evidence.BankTransactionRef) ||
                (existing is not null && existing.RefundAmount != evidence.Amount))
                return Error.Conflict("Refund.EvidenceMismatch", "Bằng chứng Payment không khớp khoản hoàn; cần đối soát.");
            // Tách nhánh chỉ đối soát khỏi nhánh áp dụng; bước kiểm tra không được tự ghi xác nhận mới.
            if (!apply) return ((AdminOrderRefundResponse?)null, changed);
            changed = evidence.ConfirmationApplied;
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (existing is null)
            {
                // Chặn dùng một mã giao dịch ngân hàng xác nhận cho hai khoản hoàn khác nhau.
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
        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (changed && notifier is not null)
            await notifier.NotifyRenterOrderChangedAsync(order.RenterId,
                new RenterOrderChangedNotification(Guid.NewGuid(), order.Id, order.Version,
                    OrderChangeTypes.RefundChanged, timeProvider.GetUtcNow()), ct);
        return (AdminRefundMapper.ToOrderRefund(order, line), changed);
    }

    // Ghi audit admin xác nhận hoàn tiền bằng ID và số tiền; không log số tài khoản hay toàn bộ chứng từ.
    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} confirmed refund transfer of {Amount} for rental item {RentalItemId} in order {OrderId}")]
    private static partial void LogRefundConfirmed(ILogger logger, Guid adminId, decimal amount, Guid rentalItemId, Guid orderId);
}
