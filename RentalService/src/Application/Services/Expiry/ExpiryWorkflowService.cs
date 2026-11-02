using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Expiry;

/// <remarks>
/// Worker gọi các hàm quét timeout; từng order/item được đọc lại dưới khóa trước khi xử lý.
/// Tách hết hạn thanh toán khỏi hết hạn supplier xác nhận; điều phối side effect qua executor và receipt.
/// </remarks>
public sealed class ExpiryWorkflowService : IExpiryWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExpiryWorkflowService> _logger;
    private readonly string _rentalEventsTopic;
    private readonly IRentalEventProducer _rentalEvents;
    private readonly IOrderHubNotifier? _notifier;
    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public ExpiryWorkflowService(
        IRentalOrderRepository orderRepository,
        IOutboxRepository outboxRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<ExpiryWorkflowService> logger,
        TimeProvider? timeProvider = null,
        IRentalEventProducer? rentalEvents = null,
        IOrderHubNotifier? notifier = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
        _rentalEvents = rentalEvents ?? new Dprental.RentalService.Application.Kafka.Producers.RentalEventProducer(
            outboxRepository,
            Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions { RentalEventsTopic = _rentalEventsTopic }),
            _timeProvider);
        _notifier = notifier;
    }
    // Lấy tối đa một batch order ứng viên quá hạn thanh toán và chuyển từng order cho executor kiểm tra lại.
    public async Task<int> ProcessPaymentTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var expiredOrderIds = await _orderRepository.GetPendingPaymentExpiredOrderIdsAsync(utcNow, 50, cancellationToken);

        // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
        int count = 0;
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        foreach (var orderId in expiredOrderIds)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var res = await ExpireSingleOrderPaymentAsync(orderId, cancellationToken);
            // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
            if (res.IsSuccess) count++;
        }

        return count;
    }

    // Quét một batch item chưa được supplier phản hồi; xử lý riêng từng item để không hết hạn nhầm các dòng khác trong order.
    public async Task<int> ProcessSupplierTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var expiredItemIds = await _orderRepository.GetPendingSupplierTimeoutItemIdsAsync(utcNow, 50, cancellationToken);

        // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
        int count = 0;
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var itemId in expiredItemIds)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var res = await ExpireSingleItemSupplierTimeoutAsync(itemId, cancellationToken);
            // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
            if (res.IsSuccess) count++;
        }

        return count;
    }

    // Sinh key PaymentExpiry ổn định theo order và dùng executor chung cho kiểm tra trạng thái cùng tác vụ hủy/nhả.
    public async Task<Result> ExpireSingleOrderPaymentAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string opKey = RentalOperationExecutor.ComputeOperationKey("PaymentExpiry", orderId.ToString());
        return await _operationExecutor.ExecutePaymentExpiryAsync(orderId, opKey, cancellationToken);
    }

    // Đọc lại item dưới khóa, đối soát accept còn treo trước khi chốt hết hạn và ghi release/refund cùng event.
    public async Task<Result> ExpireSingleItemSupplierTimeoutAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return Result.Success();

        // Giữ operation ID accept để đối soát qua executor sau khi thoát phạm vi khóa hiện tại, tránh lấy lồng cùng khóa.
        string? inFlightAcceptOpId = null;
        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order == null) return Result.Success();

            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var item = order.Items.FirstOrDefault(i => i.Id == itemId);
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (item == null || item.Status != RentalItemStatus.PendingSupplierConfirmation)
            {
                return Result.Success();
            }

            // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
            if (item.SupplierConfirmationDeadline.HasValue && utcNow < item.SupplierConfirmationDeadline.Value)
            {
                return Result.Success();
            }

            // Danh sách quét hết hạn chỉ là ứng viên: phải đọc lại trạng thái dưới khóa order.
            // Supplier có thể đã accept trước deadline nhưng kết quả Inventory chưa quay về;
            // ưu tiên đối soát intent đó, tránh vừa accept vừa release/refund cùng item.
            var inFlightAccept = order.OperationReceipts
                .FirstOrDefault(r => r.Action == "SupplierAccept" &&
                                     r.TargetItemId == item.Id &&
                                     (r.Status == RentalOperationReceipt.StatusProcessing || r.Status == RentalOperationReceipt.StatusPending) &&
                                     item.SupplierConfirmationDeadline.HasValue &&
                                     r.CreatedAt < item.SupplierConfirmationDeadline.Value);

            // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
            if (inFlightAccept != null)
            {
                // Giữ operation ID accept để đối soát qua executor sau khi thoát phạm vi khóa hiện tại, tránh lấy lồng cùng khóa.
                inFlightAcceptOpId = inFlightAccept.OperationId;
            }
            else if (order.HasInFlightAccept(item.Id))
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogInformation("Item {ItemId} has in-flight accept intent or uncertain outcome; protected from supplier timeout expiry.", itemId);
                return Result.Success();
            }
            else
            {
                // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
                string timeoutOpKey = RentalOperationExecutor.ComputeOperationKey("SupplierTimeout", $"{order.Id}:{item.Id}");
                RentalOrderStatus previousOrderStatus = order.Status;
                item.ApplySupplierTimeout(utcNow, timeoutOpKey);
                RentalOrderStatusPolicy.Apply(order, utcNow, timeoutOpKey);

                // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
                string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
                var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (releaseReceipt == null)
                {
                    // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                    var releaseReq = new ReleaseAllocationRequest(releaseOpKey, "Supplier confirmation deadline expired");
                    var relCmd = new RentalOperationCommand(
                        1,
                        "ReleaseAllocation",
                        order.Id,
                        item.Id,
                        releaseOpKey,
                        item.AllocationGroupId,
                        null,
                        JsonSerializer.Serialize(releaseReq, JsonOptions));

                    // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
                    releaseReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        order.Id,
                        releaseOpKey,
                        "ReleaseAllocation",
                        RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{item.AllocationGroupId}"),
                        isSuccess: false,
                        errorCode: null,
                        errorMessage: null,
                        createdAt: utcNow,
                        status: RentalOperationReceipt.StatusPending,
                        targetItemId: item.Id,
                        payloadJson: JsonSerializer.Serialize(relCmd, JsonOptions));
                    order.AddOperationReceipt(releaseReceipt);
                }

                // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
                string refundOpKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
                var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (refundReceipt == null)
                {
                    // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                    var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                    var refundReq = new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, "Supplier confirmation deadline expired", refundOpKey, RentalRefundKinds.CancellationRefund);
                    var refundCmd = new RentalOperationCommand(
                        1,
                        "RequestRefund",
                        order.Id,
                        item.Id,
                        refundOpKey,
                        null,
                        null,
                        JsonSerializer.Serialize(refundReq, JsonOptions));

                    // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
                    refundReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        order.Id,
                        refundOpKey,
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
                    order.AddOperationReceipt(refundReceipt);
                }

                // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
                try
                {
                    // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
                    order.RecordReadModelUpdate(utcNow);

                    // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                    var expiredRes = await _rentalEvents.EnqueueRentalItemExpiredAsync(
                        new RentalItemExpiredPayload(order.Id, item.Id, item.SupplierId, utcNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, order.Version),
                        correlationId: timeoutOpKey,
                        cancellationToken: cancellationToken);
                    // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                    if (expiredRes.IsFailure)
                    {
                        // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return expiredRes.Error;
                    }

                    // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                    if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
                    {
                        // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                        var orderCompletedRes = await _rentalEvents.EnqueueRentalOrderCompletedAsync(
                            new RentalOrderCompletedPayload(order.Id, order.RenterId, utcNow, order.Version),
                            correlationId: timeoutOpKey,
                            cancellationToken: cancellationToken);
                        // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                        if (orderCompletedRes.IsFailure)
                        {
                            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                            await tx.RollbackAsync(cancellationToken);
                            _unitOfWork.DiscardChanges();
                            return orderCompletedRes.Error;
                        }
                    }

                    // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                    if (previousOrderStatus != RentalOrderStatus.Expired && order.Status == RentalOrderStatus.Expired)
                    {
                        // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                        var orderExpired = await _rentalEvents.EnqueueRentalOrderExpiredAsync(
                            new RentalOrderExpiredPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, order.Version),
                            correlationId: timeoutOpKey,
                            cancellationToken: cancellationToken);
                        // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                        if (orderExpired.IsFailure)
                        {
                            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                            await tx.RollbackAsync(cancellationToken);
                            _unitOfWork.DiscardChanges();
                            return orderExpired.Error;
                        }
                    }

                    // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
                    await _orderRepository.UpdateAsync(order, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
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
                                OrderChangeTypes.SupplierConfirmationExpired,
                                utcNow),
                            cancellationToken);

                        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                        var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
                        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                        if (subOrder != null)
                        {
                            // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                            await _notifier.NotifySupplierSubOrderChangedAsync(
                                item.SupplierId,
                                new SupplierSubOrderChangedNotification(
                                    Guid.NewGuid(),
                                    order.Id,
                                    subOrder.Id,
                                    order.Version,
                                    OrderChangeTypes.SupplierConfirmationExpired,
                                    utcNow),
                                cancellationToken);
                        }
                    }
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
        }

        // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
        if (inFlightAcceptOpId != null)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var reconcileRes = await _operationExecutor.ExecuteReconcileAcceptAsync(orderId.Value, itemId, inFlightAcceptOpId, cancellationToken);
            // Rẽ nhánh theo Result của bước trước; truyền lỗi hoặc dùng kết quả thành công trước khi tiếp tục luồng.
            if (reconcileRes.IsSuccess)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogInformation("Reconciled in-flight accept intent for item {ItemId} instead of timing out.", itemId);
                return Result.Success();
            }
        }

        return Result.Success();
    }
}
