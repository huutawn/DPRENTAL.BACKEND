using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Payments;

/// <remarks>
/// Nhận InvoicePaid sau bước kiểm tra bản tin: đối chiếu invoice, renter, số tiền và payment reference.
/// Phân biệt thời điểm trả tiền PaidAt với thời điểm nhận message; chọn nhánh xác nhận, phục hồi hold hoặc hoàn tiền.
/// Hàm có tên Staged chỉ chuẩn bị thay đổi nghiệp vụ; dispatcher sở hữu transaction cùng Inbox và Outbox.
/// </remarks>
public sealed class PaymentWorkflowService : IPaymentWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOperationReceiptRepository _receiptRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentWorkflowService> _logger;
    private readonly string _rentalEventsTopic;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly IRentalEventProducer _rentalEventProducer;
    private readonly IOrderHubNotifier? _notifier;
    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public PaymentWorkflowService(
        IRentalOrderRepository orderRepository,
        IInboxRepository inboxRepository,
        IOutboxRepository outboxRepository,
        IOperationReceiptRepository receiptRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IUnitOfWork unitOfWork,
        IInventoryCommandProducer inventoryCommands,
        IConfiguration configuration,
        ILogger<PaymentWorkflowService> logger,
        IRentalEventProducer rentalEventProducer,
        TimeProvider? timeProvider = null,
        IOrderHubNotifier? notifier = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _receiptRepository = receiptRepository ?? throw new ArgumentNullException(nameof(receiptRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _inventoryCommands = inventoryCommands ?? throw new ArgumentNullException(nameof(inventoryCommands));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
        _rentalEventProducer = rentalEventProducer ?? throw new ArgumentNullException(nameof(rentalEventProducer));
        _notifier = notifier;
    }

    // Chuẩn bị nhánh nghiệp vụ cho InvoicePaid trong transaction do dispatcher quản lý.
    // Không đánh dấu Inbox Processed ở đây; caller phải commit cùng toàn bộ thay đổi và Outbox phát sinh.
    public async Task<Result<PaymentApplyResult>> ApplyInvoicePaidStagedAsync(
        RentalOrder order,
        KafkaMessageEnvelope<InvoicePaidPayload> envelope,
        RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default)
    {
        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        var envValidation = ValidateEnvelope(envelope);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (envValidation.IsFailure) return envValidation.Error;

        // Lấy payload của envelope đã đi qua bước kiểm tra đầu vào để xử lý cùng một bản tin xuyên suốt use case.
        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Invoice phải đã được gắn vào order; sự kiện đến sớm được giữ lại để retry sau.
        if (string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            return Error.Failure("INVOICE_NOT_ATTACHED", "Đơn hàng chưa được gắn mã invoice từ Checkout; giữ event để retry.");
        }

        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (!string.Equals(order.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("INVOICE_MISMATCH", $"InvoiceRef ({payload.InvoiceRef}) không khớp với đơn hàng ({order.InvoiceRef}).");
        }

        // Đối chiếu renter, tiền tệ và tổng tiền với snapshot của order trước khi ghi nhận thanh toán.
        if (order.RenterId != payload.RenterId)
        {
            return Error.Validation("RENTER_MISMATCH", "RenterId trong sự kiện thanh toán không khớp với đơn hàng.");
        }

        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (!string.Equals(order.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("CURRENCY_MISMATCH", $"Tiền tệ thanh toán ({payload.Currency}) không khớp với đơn hàng ({order.Currency}).");
        }

        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (order.TotalPayableAmount != payload.PaidAmount)
        {
            return Error.Validation("AMOUNT_MISMATCH", $"Số tiền thanh toán ({payload.PaidAmount}) không khớp với tổng tiền đơn hàng ({order.TotalPayableAmount}).");
        }

        // Inbox chống trùng MessageId; PaymentRef còn chống cùng khoản tiền được gửi lại
        // trong một event có MessageId khác. Hai tầng này giải quyết hai loại trùng khác nhau.
        // Cùng PaymentRef là replay; PaymentRef khác trên đơn đã nhận tiền là conflict.
        if (!string.IsNullOrWhiteSpace(order.PaymentRef))
        {
            // Đối chiếu tham chiếu thanh toán để tách khoản tiền đã ghi nhận khỏi một thanh toán khác hoặc sự kiện lặp.
            if (string.Equals(order.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success(PaymentApplyResult.Unchanged());
            }
            return Error.Conflict("PAYMENT_REF_CONFLICT", "Đơn hàng đã được thanh toán với mã tham chiếu thanh toán khác.");
        }

        // 1. Đơn đã chủ động hủy: lập lịch hoàn toàn bộ thay vì khôi phục đơn.
        if (order.Status == RentalOrderStatus.Cancelled)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogWarning("Payment arrived for cancelled Order {OrderId}. Staging full refund.", order.Id);
            order.MarkAsLatePayment(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
            order.RecordReadModelUpdate(utcNow);
            await StageFullRefundAndReleaseAsync(order, payload, utcNow, cancellationToken);
            var refundMeta = BuildPaymentMetadata(order, OrderChangeTypes.PaymentRefundRequired, payload.PaidAt, visibleToSuppliers: false);
            return Result.Success(PaymentApplyResult.RefundRequired(refundMeta));
        }

        // 2. Tính hạn phục hồi bằng mốc sớm nhất của cửa sổ thanh toán, hạn supplier và ngày bắt đầu thuê.
        DateTimeOffset paymentCutoff = order.PaymentDeadline.AddMinutes(15);
        DateTimeOffset supplierCutoff = order.Items
            .Where(i => i.SupplierConfirmationDeadline.HasValue)
            .Select(i => i.SupplierConfirmationDeadline!.Value)
            .DefaultIfEmpty(paymentCutoff)
            .Min();
        DateTimeOffset startOfDayCutoff = order.Items
            .Select(i => Clients.RentalInventoryDateAdapter.GetStartOfDayUtc(i.ExpectedStartDate))
            .Min();
        DateTimeOffset cutoff = paymentCutoff < supplierCutoff ? paymentCutoff : supplierCutoff;
        cutoff = cutoff < startOfDayCutoff ? cutoff : startOfDayCutoff;

        // PaidAt là lúc Payment ghi nhận tiền; utcNow là lúc Rental xử lý event.
        // Event tới chậm không tự động có nghĩa renter trả muộn; cửa sổ phục hồi là kiểm tra riêng.
        // 3. Phân loại thanh toán và kiểm tra hold để chọn gia hạn, giữ lại hoặc bù trừ/hoàn tiền.
        bool isLatePayment = string.Equals(payload.PaymentDisposition, "LATE_REQUIRES_REFUND", StringComparison.OrdinalIgnoreCase) ||
                             order.Status == RentalOrderStatus.Expired ||
                             payload.PaidAt >= order.PaymentDeadline;

        // Điều kiện này quyết định có thể tiếp tục hay phải trả markResult.Error theo contract của use case.
        if (!isLatePayment)
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            var markResult = order.MarkAsPaid(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (markResult.IsFailure) return markResult.Error;

            // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
            order.RecordReadModelUpdate(utcNow);

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var orderPaidResult = await _rentalEventProducer.EnqueueRentalOrderPaidAsync(
                new RentalOrderPaidPayload(order.Id, order.RenterId, payload.InvoiceRef,
                    payload.PaymentRef, order.TotalPayableAmount, payload.PaidAt, order.Version),
                correlationId: envelope.CorrelationId,
                causationId: envelope.MessageId,
                cancellationToken: cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (orderPaidResult.IsFailure) return orderPaidResult.Error;

            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (var item in order.Items)
            {
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (!item.EquipmentTypeId.HasValue || item.EquipmentTypeId.Value == Guid.Empty)
                {
                    return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", $"EquipmentTypeId của rental item {item.Id} không hợp lệ.");
                }

                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var enqueueResult = await _rentalEventProducer.EnqueueRentalItemPaidAsync(
                    new RentalItemPaidPayload(order.RenterId, item.EquipmentListingId,
                        item.EquipmentTypeId, order.Id, item.Id, order.Version),
                    correlationId: envelope.CorrelationId,
                    causationId: envelope.MessageId,
                    cancellationToken: cancellationToken);
                // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
                if (enqueueResult.IsFailure) return enqueueResult.Error;
                var confirmationResult = await _rentalEventProducer.EnqueueRentalSupplierConfirmationRequestedAsync(
                    new RentalSupplierConfirmationRequestedPayload(
                        order.Id,
                        item.Id,
                        item.SupplierId,
                        item.EquipmentListingId,
                        item.Quantity,
                        item.SupplierConfirmationDeadline ?? utcNow.AddHours(24),
                        order.Version,
                        order.OrderCode),
                    correlationId: envelope.CorrelationId,
                    causationId: envelope.MessageId,
                    cancellationToken: cancellationToken);
                if (confirmationResult.IsFailure) return confirmationResult.Error;
                string opKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
                var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (existingReceipt == null)
                {
                    // Tính hạn hold phục vụ xác nhận theo deadline của item/cutoff; không dùng một hạn mới tùy ý khi retry.
                    DateTimeOffset newHoldExpiresAt = item.SupplierConfirmationDeadline is DateTimeOffset deadline
                        ? RentalItem.CalculateConfirmationHoldExpiry(deadline, item.ExpectedStartDate)
                        : payload.PaidAt.AddHours(24);
                    var extHoldReq = new ExtendHoldRequest(newHoldExpiresAt, opKey);
                    var extHoldCmd = new RentalOperationCommand(
                        1,
                        "ExtendHold",
                        order.Id,
                        item.Id,
                        opKey,
                        item.AllocationGroupId,
                        null,
                        JsonSerializer.Serialize(extHoldReq, JsonOptions));

                    // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                    var receipt = RentalOperationReceipt.CreatePending(
                        Guid.NewGuid(),
                        order.Id,
                        opKey,
                        "ExtendHold",
                        RentalOperationExecutor.ComputeFingerprint($"ExtendHold:{item.AllocationGroupId}:{newHoldExpiresAt:O}"),
                        utcNow,
                        item.Id,
                        JsonSerializer.Serialize(extHoldCmd, JsonOptions));

                    // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                    order.AddOperationReceipt(receipt);

                    // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                    var extendCmdPayload = new ExtendHoldCommandPayload(
                        order.Id,
                        item.Id,
                        opKey,
                        newHoldExpiresAt,
                        item.AllocationGroupId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (_inventoryCommands != null)
                    {
                        // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                        await _inventoryCommands.EnqueueExtendHoldAsync(extendCmdPayload, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                    }
                }
            }
        }
        else
        {
            // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
            if (utcNow >= cutoff)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogWarning("Payment arrived after recovery cutoff for Order {OrderId}. Cannot recover. Staging full refund.", order.Id);
                order.MarkAsLatePayment(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
                order.RecordReadModelUpdate(utcNow);
                await StageFullRefundAndReleaseAsync(order, payload, utcNow, cancellationToken);
                var refundMeta = BuildPaymentMetadata(order, OrderChangeTypes.PaymentRefundRequired, payload.PaidAt, visibleToSuppliers: false);
                return Result.Success(PaymentApplyResult.RefundRequired(refundMeta));
            }

            // Nhánh phục hồi chỉ ghi nhận đã nhận tiền và yêu cầu hold mới. Chưa thông báo
            // supplier là đơn đã sẵn sàng: còn phải chờ Inventory xác nhận lại đủ allocation.
            _logger.LogInformation("Payment arrived late/hold expired for Order {OrderId}. Initiating recovery re-allocation before cutoff {Cutoff}.", order.Id, cutoff);
            order.RecordPaymentReceived(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
            order.RecordReadModelUpdate(utcNow);
            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (var item in order.Items)
            {
                // Dùng nhóm recovery riêng để không nhầm hold cũ đã hết hạn với hold mới.
                // Key ổn định theo order/item giúp các lần giao lại event không tạo thêm nhóm recovery.
                string recGroupId = $"rental-item:{item.Id}:rec-1";
                string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{order.Id}:{item.Id}");
                var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == recOpKey);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (existingReceipt == null)
                {
                    // Tính hạn hold phục vụ xác nhận theo deadline của item/cutoff; không dùng một hạn mới tùy ý khi retry.
                    DateTimeOffset holdExpiry = item.SupplierConfirmationDeadline is DateTimeOffset deadline
                        ? RentalItem.CalculateConfirmationHoldExpiry(deadline, item.ExpectedStartDate)
                        : cutoff;

                    // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                    var createHoldReq = new CreateHoldCommandPayload(
                        order.Id,
                        item.Id,
                        recOpKey,
                        item.EquipmentListingId,
                        item.Quantity,
                        item.ExpectedStartDate,
                        item.ExpectedEndDate,
                        holdExpiry,
                        Notes: "Recovery hold after payment",
                        AllocationGroupId: recGroupId,
                        ExecutionCutoffUtc: cutoff);

                    // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                    var receipt = RentalOperationReceipt.CreatePending(
                        Guid.NewGuid(),
                        order.Id,
                        recOpKey,
                        "RecoveryHold",
                        RentalOperationExecutor.ComputeFingerprint($"RecoveryHold:{recGroupId}"),
                        utcNow,
                        item.Id,
                        JsonSerializer.Serialize(createHoldReq, JsonOptions));

                    // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                    order.AddOperationReceipt(receipt);

                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (_inventoryCommands != null)
                    {
                        // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                        await _inventoryCommands.EnqueueCreateHoldAsync(createHoldReq, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                    }
                }
            }
            var recMeta = BuildPaymentMetadata(order, OrderChangeTypes.PaymentRecoveryPending, payload.PaidAt, visibleToSuppliers: false);
            return Result.Success(PaymentApplyResult.RecoveryPending(recMeta));
        }
        var paidMeta = BuildPaymentMetadata(order, OrderChangeTypes.PaymentConfirmed, payload.PaidAt, visibleToSuppliers: true);
        return Result.Success(PaymentApplyResult.Paid(paidMeta));
    }

    // Dựng thông báo cho renter và, khi được phép, các supplier; không gửi thông báo ở bước stage transaction.
    private static PaymentNotificationMetadata BuildPaymentMetadata(
        RentalOrder order,
        string changeType,
        DateTimeOffset occurredAt,
        bool visibleToSuppliers)
    {
        // Dựng response/thông báo từ dữ liệu vừa xử lý; không tự thay đổi trạng thái nghiệp vụ trong bước ánh xạ.
        var renterNotification = new RenterOrderChangedNotification(
            Guid.NewGuid(),
            order.Id,
            order.Version,
            changeType,
            occurredAt);

        // Chỉ dựng tập thông báo supplier khi đơn được phép hiển thị; nhánh recovery/refund giữ danh sách này rỗng.
        List<(Guid SupplierId, SupplierSubOrderChangedNotification Notification)> supplierNotifications;
        // Chỉ dựng thông báo supplier khi trạng thái thanh toán cho phép họ nhìn thấy đơn.
        if (visibleToSuppliers)
        {
            // Chỉ dựng tập thông báo supplier khi đơn được phép hiển thị; nhánh recovery/refund giữ danh sách này rỗng.
            supplierNotifications = order.SubOrders
                .Select(s => (s.SupplierId, new SupplierSubOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    s.Id,
                    order.Version,
                    changeType,
                    occurredAt)))
                .ToList();
        }
        else
        {
            // Chỉ dựng tập thông báo supplier khi đơn được phép hiển thị; nhánh recovery/refund giữ danh sách này rỗng.
            supplierNotifications = [];
        }

        return new PaymentNotificationMetadata(renterNotification, supplierNotifications);
    }

    // Lập release/refund theo từng item với key ổn định; journal lưu request để worker đối soát khoản tiền sau đó.
    private async Task StageFullRefundAndReleaseAsync(
        RentalOrder order,
        InvoicePaidPayload payload,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var item in order.Items)
        {
            // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
            string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
            var existingRelease = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (existingRelease == null)
            {
                // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                var releaseReq = new ReleaseAllocationRequest(releaseOpKey, "Late payment arrived after deadline; releasing allocation");
                var releaseCmd = new RentalOperationCommand(
                    1,
                    "ReleaseAllocation",
                    order.Id,
                    item.Id,
                    releaseOpKey,
                    item.AllocationGroupId,
                    null,
                    JsonSerializer.Serialize(releaseReq, JsonOptions));

                // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                var releaseReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(),
                    order.Id,
                    releaseOpKey,
                    "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{item.AllocationGroupId}"),
                    utcNow,
                    item.Id,
                    JsonSerializer.Serialize(releaseCmd, JsonOptions));

                // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                order.AddOperationReceipt(releaseReceipt);

                // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                var releaseCmdPayload = new ReleaseAllocationCommandPayload(
                    order.Id,
                    item.Id,
                    releaseOpKey,
                    "Late payment arrived after deadline; releasing allocation",
                    item.AllocationGroupId);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (_inventoryCommands != null)
                {
                    // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                    await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmdPayload, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                }
            }

            // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
            string refundKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
            var existingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundKey);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (existingRefund == null)
            {
                // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                string refundKind = payload.PaidAt >= order.PaymentDeadline
                    ? RentalRefundKinds.LatePaymentRefund
                    : RentalRefundKinds.CancellationRefund;
                var refundReq = new RequestRefundRequest(order.Id, payload.InvoiceRef, payload.PaymentRef, refundItems, "Payment arrived on terminal/expired order; 100% refund", refundKey, refundKind);
                var refundCmd = new RentalOperationCommand(
                    1,
                    "RequestRefund",
                    order.Id,
                    item.Id,
                    refundKey,
                    null,
                    null,
                    JsonSerializer.Serialize(refundReq, JsonOptions));

                // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
                var refundReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    refundKey,
                    "RequestRefund",
                    RentalOperationExecutor.ComputeFingerprint($"RequestRefund:{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    targetItemId: item.Id,
                    prerequisiteOperationId: releaseOpKey,
                    payloadJson: JsonSerializer.Serialize(refundCmd, JsonOptions));

                // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                order.AddOperationReceipt(refundReceipt);
            }
        }

        // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
        var enqRes = await _rentalEventProducer.EnqueueLatePaymentRefundRequestedAsync(
            new LatePaymentRefundRequestedPayload(
                order.Id,
                order.RenterId,
                payload.InvoiceRef,
                payload.PaymentRef,
                order.TotalPayableAmount,
                utcNow,
                order.Version),
            correlationId: order.Id.ToString("D"),
            cancellationToken: cancellationToken);
        // Trả lỗi từ bước kiểm tra vừa chạy; không thực hiện phần ghi hoặc gọi provider tiếp theo khi điều kiện chưa đạt.
        if (enqRes.IsFailure)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogWarning("Failed to enqueue LatePaymentRefundRequested event for Order {OrderId}: {Error}", order.Id, enqRes.Error.Message);
        }
    }

    // Validate envelope, khóa order và deduplicate Inbox; áp dụng thanh toán rồi commit Inbox/Order/Outbox trước thông báo.
    public async Task<Result> ProcessInvoicePaidAsync(
        KafkaMessageEnvelope<InvoicePaidPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        var envValidation = ValidateEnvelope(envelope);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (envValidation.IsFailure) return envValidation;

        // Lấy payload của envelope đã đi qua bước kiểm tra đầu vào để xử lý cùng một bản tin xuyên suốt use case.
        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Giữ aggregate order dùng xuyên suốt phạm vi xử lý; dữ liệu phải được đọc theo repository/khóa trước khi cập nhật.
        RentalOrder? order;
        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(payload.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // Đóng gói snapshot/payload đang xử lý để lưu hoặc truyền theo contract; giữ dữ liệu của lần quyết định hiện tại.
            string rawPayload = JsonSerializer.Serialize(envelope, JsonOptions);
            string fingerprint = ComputeHash(rawPayload);

            // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
            var existingInbox = await _inboxRepository.GetByMessageIdAsync(envelope.MessageId, cancellationToken);
            // Inbox đã Processed thì bản tin là replay theo MessageId; không áp dụng nghiệp vụ lần thứ hai.
            if (existingInbox != null && existingInbox.Status == RentalInboxMessage.StatusProcessed)
            {
                return Result.Success();
            }

            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            order = await _orderRepository.GetByIdWithDetailsAsync(payload.RentalOrderId, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order == null)
            {
                return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{payload.RentalOrderId}'.");
            }

            // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
            var inboxMessage = existingInbox ?? new RentalInboxMessage(
                Guid.NewGuid(),
                envelope.MessageId,
                envelope.MessageType,
                envelope.Producer,
                envelope.AggregateId,
                envelope.CorrelationId,
                envelope.CausationId,
                rawPayload,
                utcNow,
                fingerprint);

            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (existingInbox == null)
            {
                // Đăng ký Inbox mới trong cùng transaction áp dụng bản tin để chống xử lý lặp sau khi consumer khởi động lại.
                await _inboxRepository.AddAsync(inboxMessage, cancellationToken);
            }

            // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
            try
            {
                // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
                var result = await ApplyInvoicePaidStagedAsync(order, envelope, inboxMessage, cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (result.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return result.Error;
                }

                // Đánh dấu Inbox đã áp dụng; trạng thái này phải được lưu cùng các thay đổi nghiệp vụ của bản tin.
                inboxMessage.MarkProcessed(utcNow);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (existingInbox != null)
                {
                    // Đăng ký thay đổi Inbox cùng phần ghi nghiệp vụ, để trạng thái Processed không được lưu tách rời kết quả áp dụng.
                    _inboxRepository.Update(inboxMessage);
                }

                // Chỉ đăng ký cập nhật order khi bước áp dụng báo có thay đổi; Inbox vẫn cần được đánh dấu đã xử lý.
                if (result.Value.Changed)
                {
                    // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
                    await _orderRepository.UpdateAsync(order, cancellationToken);
                }

                // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
                if (_notifier != null && result.Value.Metadata is { } meta)
                {
                    // Gửi notification renter từ metadata sau commit; không gửi thông báo từ trong phần stage nghiệp vụ.
                    if (meta.RenterNotification is { } renterNote)
                    {
                        // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                        await _notifier.NotifyRenterOrderChangedAsync(order.RenterId, renterNote, cancellationToken);
                    }

                    // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
                    foreach (var (supplierId, supplierNote) in meta.SupplierNotifications)
                    {
                        // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                        await _notifier.NotifySupplierSubOrderChangedAsync(supplierId, supplierNote, cancellationToken);
                    }
                }
            }
            // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
            catch (Exception)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                throw;
            }
        }

        return Result.Success();
    }

    // Kiểm tra version, producer, aggregate và dữ liệu tiền/thời gian trước khi dùng sự kiện Payment để đổi trạng thái đơn.
    private static Result ValidateEnvelope(KafkaMessageEnvelope<InvoicePaidPayload> envelope)
    {
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (envelope == null) return Error.Validation("INVALID_ENVELOPE", "Envelope không được rỗng.");
        // Dừng nhánh không hợp lệ với mã UNSUPPORTED_VERSION; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (envelope.Version != 1) return Error.Validation("UNSUPPORTED_VERSION", $"Chỉ hỗ trợ envelope version 1 (nhận {envelope.Version}).");
        // Dừng nhánh không hợp lệ với mã INVALID_MESSAGE_TYPE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!string.Equals(envelope.MessageType, "InvoicePaid", StringComparison.OrdinalIgnoreCase)) return Error.Validation("INVALID_MESSAGE_TYPE", $"MessageType không hợp lệ: {envelope.MessageType}.");
        // Dừng nhánh không hợp lệ với mã INVALID_PRODUCER; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!string.Equals(envelope.Producer, "payment-service", StringComparison.OrdinalIgnoreCase)) return Error.Validation("INVALID_PRODUCER", $"Producer không hợp lệ: {envelope.Producer}.");
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (envelope.Payload == null) return Error.Validation("INVALID_PAYLOAD", "Payload không được rỗng.");
        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!Guid.TryParse(envelope.AggregateId, out var aggId) || aggId != envelope.Payload.RentalOrderId) return Error.Validation("AGGREGATE_ID_MISMATCH", "AggregateId không khớp với RentalOrderId.");
        // Dừng nhánh không hợp lệ với mã INVALID_ORDER_ID; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (envelope.Payload.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_ORDER_ID", "RentalOrderId không được để trống.");
        // Dừng nhánh không hợp lệ với mã INVALID_RENTER_ID; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (envelope.Payload.RenterId == Guid.Empty) return Error.Validation("INVALID_RENTER_ID", "RenterId không được để trống.");
        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (string.IsNullOrWhiteSpace(envelope.Payload.InvoiceRef)) return Error.Validation("INVALID_INVOICE_REF", "InvoiceRef không được để trống.");
        // Đối chiếu tham chiếu thanh toán để tách khoản tiền đã ghi nhận khỏi một thanh toán khác hoặc sự kiện lặp.
        if (string.IsNullOrWhiteSpace(envelope.Payload.PaymentRef)) return Error.Validation("INVALID_PAYMENT_REF", "PaymentRef không được để trống.");
        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (!string.Equals(envelope.Payload.Currency, "VND", StringComparison.OrdinalIgnoreCase)) return Error.Validation("INVALID_CURRENCY", $"Tiền tệ không hợp lệ: {envelope.Payload.Currency}.");
        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (envelope.Payload.PaidAmount < 0) return Error.Validation("INVALID_PAID_AMOUNT", "Số tiền thanh toán không được âm.");
        // Dừng nhánh không hợp lệ với mã INVALID_PAID_AT; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (envelope.Payload.PaidAt == default) return Error.Validation("INVALID_PAID_AT", "Thời gian thanh toán không hợp lệ.");
        // Dừng nhánh không hợp lệ với mã INVALID_PAYMENT_DISPOSITION; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!string.Equals(envelope.Payload.PaymentDisposition, "ON_TIME", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(envelope.Payload.PaymentDisposition, "LATE_REQUIRES_REFUND", StringComparison.OrdinalIgnoreCase))
            return Error.Validation("INVALID_PAYMENT_DISPOSITION", "PaymentDisposition không được hỗ trợ.");

        return Result.Success();
    }

    // Băm payload đã serialize để lưu fingerprint bản tin trong Inbox.
    private static string ComputeHash(string input)
    {
        // Tạo fingerprint nội dung để nhận diện payload đã lưu; không dùng fingerprint thay cho kiểm tra quyền sở hữu.
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
