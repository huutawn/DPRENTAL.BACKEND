using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    private const string CancelOrderAction = "CancelOrder";
    private const string DefaultRenterCancelReason = "Cancelled by renter before payment";
    private const string DefaultPaidCancelReason = "Cancelled by renter before supplier confirmation";
    private const int MaxCancelReasonLength = 500;

    /// <summary>
    /// Renter hủy cả đơn khi đang PendingPayment, hoặc đã thanh toán mà chưa nhà cung cấp nào xác nhận.
    /// Thứ tự với PendingPayment: kiểm tra quyền sở hữu, khóa order, idempotency, kiểm tra trạng thái,
    /// hủy invoice (hoặc chặn nếu invoice đang được tạo), xếp lệnh release từng allocation vào outbox,
    /// rồi mới lưu trạng thái Cancelled, receipt và outbox event trong một lần commit.
    /// Đơn đã thanh toán đi theo <see cref="CancelPaidOrderBeforeConfirmationAsync"/>.
    /// </summary>
    public async Task<Result<CancelOrderResponse>> ExecuteRenterCancelOrderAsync(
        Guid orderId,
        Guid renterId,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Dừng nhánh không hợp lệ với mã AUTH_REQUIRED; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (renterId == Guid.Empty)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu danh tính người thuê hợp lệ.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và tối đa 64 ký tự.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? DefaultRenterCancelReason : reason.Trim();
        // Dừng nhánh không hợp lệ với mã INVALID_CANCEL_REASON; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (normalizedReason.Length > MaxCancelReasonLength)
        {
            return Error.Validation("INVALID_CANCEL_REASON", $"Lý do hủy tối đa {MaxCancelReasonLength} ký tự.");
        }

        // Kiểm tra sở hữu trước khi giữ lock để người ngoài không chiếm lock của đơn người khác.
        Guid? ownerId = await _orderRepository.GetRenterIdByOrderIdAsync(orderId, cancellationToken);
        // Dừng nhánh không hợp lệ với mã Order.NotFound; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (ownerId != renterId)
        {
            return Error.NotFound("Order.NotFound", "Không tìm thấy đơn hàng.");
        }

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null || order.RenterId != renterId)
        {
            return Error.NotFound("Order.NotFound", "Không tìm thấy đơn hàng.");
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string fingerprint = ComputeFingerprint(new
        {
            Action = CancelOrderAction,
            OrderId = orderId,
            RenterId = renterId,
            Reason = normalizedReason
        });

        // 1. Idempotency: cùng khóa + cùng nội dung trả lại kết quả đã lưu.
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReceipt != null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (existingReceipt.Action != CancelOrderAction ||
                !string.Equals(existingReceipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (existingReceipt.IsSuccess &&
                existingReceipt.Status == RentalOperationReceipt.StatusSucceeded &&
                !string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
            {
                // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
                try
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    var stored = JsonSerializer.Deserialize<CancelOrderResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (stored != null) return stored;
                }
                // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
                catch (JsonException)
                {
                    return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                }
            }

            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
        }

        // 2. Chỉ hủy được khi chưa thanh toán, hoặc đã thanh toán mà chưa nhà cung cấp nào xác nhận.
        if (!RenterOrderCancellationPolicy.CanCancel(order.Status, order.Items.Select(i => i.Status)))
        {
            return RentalErrors.RenterOrderCancellationNotAllowed;
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (order.Status == RentalOrderStatus.Paid)
        {
            // Chọn lý do mặc định phù hợp nhánh hủy đơn đã thanh toán, giữ lý do client nếu có nội dung hợp lệ.
            string paidCancelReason = string.IsNullOrWhiteSpace(reason) ? DefaultPaidCancelReason : normalizedReason;
            return await CancelPaidOrderBeforeConfirmationAsync(order, renterId, paidCancelReason, normalizedKey, fingerprint, cancellationToken);
        }

        // 3. Invoice: hủy nếu đã có InvoiceRef; nếu chưa có thì phải chứng minh invoice không đang được tạo.
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
            string cancelOpKey = ComputeOperationKey("CancelInvoice", order.Id.ToString());
            var cancelReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == cancelOpKey);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (cancelReceipt == null)
            {
                // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
                cancelReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    cancelOpKey,
                    "CancelInvoice",
                    ComputeOperationKey("CancelInvoice", $"{order.Id}:{order.InvoiceRef}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    payloadJson: JsonSerializer.Serialize(new CancelInvoiceRequest(order.Id, order.InvoiceRef, normalizedReason, cancelOpKey), JsonOptions));
                order.AddOperationReceipt(cancelReceipt);
                await CommitLocalChangesAsync(order, cancellationToken);
            }

            // Xử lý hủy invoice trước khi chốt hủy/quá hạn; Payment báo đã thu tiền thì không tiếp tục như đơn chưa trả tiền.
            var cancelRes = await CancelInvoiceInternalAsync(order, cancelReceipt, cancelOpKey, cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (cancelRes.IsFailure)
            {
                // Điều kiện này quyết định có thể tiếp tục hay phải trả Error.Conflict theo contract của use case.
                if (string.Equals(cancelRes.Error.Code, PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, StringComparison.OrdinalIgnoreCase))
                {
                    // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                    _logger.LogInformation("Invoice was already paid for Order {OrderId}. Halting renter cancel and waiting for InvoicePaid event.", order.Id);
                    return Error.Conflict(PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, "Hóa đơn đã được thanh toán; không thể hủy đơn, hệ thống sẽ đối soát thanh toán.");
                }

                return cancelRes.Error;
            }
        }
        else
        {
            // Điều kiện này quyết định có thể tiếp tục hay phải trả Error.ServiceUnavailable theo contract của use case.
            if (!IsInvoiceCreationSafeToAbandon(order))
            {
                return Error.ServiceUnavailable(
                    "Order.InvoiceCreationInProgress",
                    "Hóa đơn của đơn hàng đang được tạo hoặc chưa rõ kết quả; vui lòng thử lại cùng Idempotency-Key sau ít phút.");
            }

            // Đóng intent tạo invoice chỉ sau khi đã chứng minh an toàn để bỏ; tránh worker tạo hóa đơn cho order đã hủy.
            AbandonPendingInvoiceCreation(order, utcNow);
            await CommitLocalChangesAsync(order, cancellationToken);
        }

        // 4. Release từng allocation (cùng operation key với luồng payment-expiry để không tạo trùng).
        var releaseKeys = new List<(RentalItem item, string releaseKey)>();
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var item in order.Items)
        {
            // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
            string releaseKey = ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
            var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseKey);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (releaseReceipt == null)
            {
                // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
                releaseReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    releaseKey,
                    "ReleaseAllocation",
                    ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    targetItemId: item.Id,
                    payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseKey, normalizedReason), JsonOptions));
                releaseReceipt.SetInventoryOperationKey(
                    ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"));
                order.AddOperationReceipt(releaseReceipt);
            }

            // Giữ danh sách cặp item/key để bước thực thi release sử dụng đúng journal đã được ghi trước đó.
            releaseKeys.Add((item, releaseKey));
        }

        // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
        await CommitLocalChangesAsync(order, cancellationToken);

        // Theo dõi kết quả lập lệnh release cho mọi dòng; không nhầm thành công enqueue với xác nhận allocation đã đóng.
        bool allReleaseCommandsQueued = true;
        foreach (var (item, releaseKey) in releaseKeys)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var releaseReceipt = order.OperationReceipts.First(r => r.OperationId == releaseKey);
            var relRes = await ReleaseAllocationInternalAsync(order, item, releaseReceipt, releaseKey, cancellationToken);
            // Trả lỗi từ bước kiểm tra vừa chạy; không thực hiện phần ghi hoặc gọi provider tiếp theo khi điều kiện chưa đạt.
            if (relRes.IsFailure) allReleaseCommandsQueued = false;
        }

        // Dừng nhánh không hợp lệ với mã RELEASE_PENDING; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!allReleaseCommandsQueued)
        {
            return Error.ServiceUnavailable("RELEASE_PENDING", "Một số lệnh giải phóng phân bổ chưa được gửi; vui lòng thử lại cùng Idempotency-Key.");
        }

        // 5. Chỉ đánh dấu Cancelled sau khi invoice đã hủy (hoặc không có) và mọi release đã được enqueue.
        // Inventory xác nhận bất đồng bộ; trạng thái assignment vẫn được giữ cho đến callback thành công.
        utcNow = _timeProvider.GetUtcNow();
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Áp dụng quyết định trạng thái đã qua các điều kiện của luồng; sau đó lưu history/receipt và side effect liên quan.
            order.MarkCancelled(utcNow, normalizedKey, normalizedReason, renterId, ActorRole.Renter);
            order.RecordReadModelUpdate(utcNow);

            // Dựng response/thông báo từ dữ liệu vừa xử lý; không tự thay đổi trạng thái nghiệp vụ trong bước ánh xạ.
            var response = new CancelOrderResponse(order.Id, order.OrderCode, order.Status.ToString(), utcNow);

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                normalizedKey,
                CancelOrderAction,
                fingerprint,
                isSuccess: true,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusSucceeded,
                completedAt: utcNow,
                apiResponseJson: JsonSerializer.Serialize(response, JsonOptions)));

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var cancelOrderRes = await _rentalEvents.EnqueueRentalOrderCancelledAsync(
                new RentalOrderCancelledPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.RenterCancelled, order.OrderCode, order.Version),
                correlationId: normalizedKey,
                cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (cancelOrderRes.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return cancelOrderRes.Error;
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await CommitLocalChangesAsync(order, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (_notifier != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await _notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.OrderCancelled,
                        utcNow),
                    cancellationToken);
            }
            return response;
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    /// <summary>
    /// Đơn đã thanh toán, chưa nhà cung cấp nào xác nhận: hủy mọi dòng đang chờ xác nhận, nhả lịch và hoàn 100%
    /// tiền thuê lẫn tiền cọc của từng dòng (cùng đường release/refund với supplier từ chối).
    /// Supplier accept cũng giữ khóa đơn nên không chạy chen; chỉ còn chặn lệnh accept đang chờ Inventory xác nhận.
    /// </summary>
    private async Task<Result<CancelOrderResponse>> CancelPaidOrderBeforeConfirmationAsync(
        RentalOrder order,
        Guid renterId,
        string reason,
        string normalizedKey,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        // Chỉ lấy các dòng còn chờ supplier xác nhận cho nhánh hủy đơn đã trả tiền; các dòng ở trạng thái khác không bị đưa vào cùng quyết định.
        List<RentalItem> pendingItems = order.Items
            .Where(i => i.Status == RentalItemStatus.PendingSupplierConfirmation)
            .ToList();

        // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
        if (pendingItems.Any(i => order.HasInFlightAccept(i.Id)))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Nhà cung cấp đang xác nhận đơn; vui lòng tải lại đơn hàng.");
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        decimal refundAmount = pendingItems.Sum(i => i.RentalAmount + i.DepositAmount);

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (RentalItem item in pendingItems)
            {
                // Áp dụng quyết định trạng thái đã qua các điều kiện của luồng; sau đó lưu history/receipt và side effect liên quan.
                item.ApplyCancelled(utcNow, normalizedKey, reason, renterId, ActorRole.Renter);
                await ScheduleReleaseAndRefundAsync(order, item, renterId, reason, utcNow, cancellationToken);
            }

            // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);
            order.RecordReadModelUpdate(utcNow);

            // Dựng response/thông báo từ dữ liệu vừa xử lý; không tự thay đổi trạng thái nghiệp vụ trong bước ánh xạ.
            var response = new CancelOrderResponse(order.Id, order.OrderCode, order.Status.ToString(), utcNow, refundAmount);

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                normalizedKey,
                CancelOrderAction,
                fingerprint,
                isSuccess: true,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusSucceeded,
                completedAt: utcNow,
                apiResponseJson: JsonSerializer.Serialize(response, JsonOptions)));

            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (RentalItem item in pendingItems)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var itemCancelledRes = await _rentalEvents.EnqueueRentalItemCancelledAsync(
                    new RentalItemCancelledPayload(order.Id, item.Id, renterId, ActorRole.Renter.ToString(), utcNow,
                        KafkaContractSpec.ReasonCodes.RenterCancelled, item.RentalAmount + item.DepositAmount, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (itemCancelledRes.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return itemCancelledRes.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (order.Status == RentalOrderStatus.Cancelled)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var cancelOrderRes = await _rentalEvents.EnqueueRentalOrderCancelledAsync(
                    new RentalOrderCancelledPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.RenterCancelled, order.OrderCode, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (cancelOrderRes.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return cancelOrderRes.Error;
                }
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await CommitLocalChangesAsync(order, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (_notifier != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await _notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.OrderCancelled,
                        utcNow),
                    cancellationToken);

                // Xử lý riêng từng đơn con thuộc order; không trộn thông tin hoặc thông báo giữa các supplier.
                foreach (RentalSubOrder subOrder in order.SubOrders.Where(s => pendingItems.Any(i => i.SupplierId == s.SupplierId)))
                {
                    // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                    await _notifier.NotifySupplierSubOrderChangedAsync(
                        subOrder.SupplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder.Id,
                            order.Version,
                            OrderChangeTypes.OrderCancelled,
                            utcNow),
                        cancellationToken);
                }
            }

            return response;
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    /// <summary>
    /// Khi chưa có InvoiceRef, chỉ coi là an toàn để hủy nếu CHỨNG MINH được chưa có lần gọi tạo invoice nào
    /// có thể đã thành công phía Payment: mọi receipt CreateInvoice chưa từng attempt, hoặc đã thất bại
    /// dứt khoát (Business, không uncertain). Không có receipt CreateInvoice nào, hoặc receipt đang Processing,
    /// uncertain, retry-transient, exhausted => không an toàn, vì Payment có thể đã tạo invoice mà Rental chưa lưu InvoiceRef.
    /// </summary>
    private static bool IsInvoiceCreationSafeToAbandon(RentalOrder order)
    {
        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        var createReceipts = order.OperationReceipts.Where(r => r.Action == "CreateInvoice").ToList();
        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (createReceipts.Count == 0)
        {
            return false;
        }

        // Xử lý từng receipt theo journal đã lưu, giữ định danh và trạng thái riêng của mỗi tác vụ.
        foreach (var r in createReceipts)
        {
            // Chứng minh chưa gọi provider bằng cả trạng thái Pending, số attempt, thời điểm attempt và cờ outcome bất định.
            bool neverAttempted =
                r.Status == RentalOperationReceipt.StatusPending &&
                r.RetryCount == 0 &&
                !r.LastAttemptedAt.HasValue &&
                !r.IsOutcomeUncertain;

            // Chỉ lỗi nghiệp vụ chắc chắn và không có outcome bất định mới chứng minh an toàn để bỏ tác vụ tạo invoice.
            bool definitivelyFailed =
                r.Status == RentalOperationReceipt.StatusFailed &&
                r.ErrorClassification == RentalOperationReceipt.ClassificationBusiness &&
                !r.IsOutcomeUncertain;

            // Điều kiện này quyết định có thể tiếp tục hay phải trả false theo contract của use case.
            if (!neverAttempted && !definitivelyFailed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Đóng các receipt CreateInvoice chưa attempt để worker không tạo invoice cho đơn đã hủy.
    /// Chỉ gọi sau khi <see cref="IsInvoiceCreationSafeToAbandon"/> trả true.
    /// </summary>
    private static void AbandonPendingInvoiceCreation(RentalOrder order, DateTimeOffset utcNow)
    {
        // Xử lý từng receipt theo journal đã lưu, giữ định danh và trạng thái riêng của mỗi tác vụ.
        foreach (var r in order.OperationReceipts.Where(r =>
                     r.Action == "CreateInvoice" && r.Status == RentalOperationReceipt.StatusPending))
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            r.MarkFailed(
                "ORDER_CANCELLED",
                "Order cancelled by renter before invoice creation was attempted.",
                utcNow,
                RentalOperationReceipt.ClassificationBusiness,
                isTransient: false);
        }
    }
}
