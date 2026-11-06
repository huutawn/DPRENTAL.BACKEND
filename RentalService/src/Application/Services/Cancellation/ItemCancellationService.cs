using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Services.Cancellation;

/// <remarks>
/// Hai điểm vào cùng xử lý item chưa bàn giao: renter yêu cầu hủy hoặc worker kết thúc khi quá hạn.
/// Kiểm tra quyền/trạng thái và tác vụ bàn giao còn treo trước khi điều phối release, refund, receipt và event.
/// Không được suy diễn máy chưa giao chỉ vì chưa nhận được phản hồi Inventory.
/// </remarks>
public sealed class ItemCancellationService : IItemCancellationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IRentalEventProducer _rentalEvents;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ItemCancellationService> _logger;
    private readonly int _autoExpireGraceHours;
    private readonly IOrderHubNotifier? _notifier;
    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public ItemCancellationService(
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null,
        ILogger<ItemCancellationService>? logger = null,
        IOptions<HandoverOverdueOptions>? handoverOptions = null,
        IRentalEventProducer? rentalEvents = null,
        IOrderHubNotifier? notifier = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _rentalEvents = rentalEvents ?? new Dprental.RentalService.Application.Kafka.Producers.RentalEventProducer(
            _outboxRepository,
            Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions { RentalEventsTopic = KafkaContractSpec.Topics.RentalEvents }),
            timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemCancellationService>.Instance;
        _autoExpireGraceHours = (handoverOptions?.Value ?? new HandoverOverdueOptions()).AutoExpireGraceHours;
        _notifier = notifier;
    }

    // Hủy item chưa được bàn giao sau mốc cho phép; kiểm tra quyền/replay và intent bàn giao trước khi lên lịch release/refund.
    public async Task<Result<CancelNotDeliveredResponse>> CancelByRenterAsync(
        Guid itemId,
        CancelNotDeliveredRequest? request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key bắt buộc và không quá 64 ký tự.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string? reason = request?.Reason?.Trim();
        // Dừng nhánh không hợp lệ với mã INVALID_REASON; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (reason?.Length > 500)
        {
            return Error.Validation("INVALID_REASON", "Lý do hủy không được vượt quá 500 ký tự.");
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        Guid? orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue)
        {
            return RentalErrors.ItemNotFound;
        }

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(15), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null)
        {
            return RentalErrors.ItemNotFound;
        }

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        var auth = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure)
        {
            return auth.Error.Type == ErrorType.NotFound ? RentalErrors.ItemNotFound : auth.Error;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = RentalItem.CancelNotDeliveredAction,
            ItemId = item.Id,
            Reason = reason
        });

        // 5. Kiểm tra receipt cùng key
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReceipt != null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (existingReceipt.Fingerprint == fingerprint)
            {
                // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
                if (!string.IsNullOrEmpty(existingReceipt.ApiResponseJson))
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    var cached = JsonSerializer.Deserialize<CancelNotDeliveredResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (cached != null) return cached;
                }
                return new CancelNotDeliveredResponse(item.Id, item.Status.ToString(), existingReceipt.CreatedAt, item.RentalAmount + item.DepositAmount);
            }
            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được dùng với nội dung khác.");
        }

        // 6. Dòng đã Cancelled từ trước với key khác -> trả kết quả cũ (lũy đẳng)
        if (item.Status == RentalItemStatus.Cancelled)
        {
            // Tìm receipt hủy thành công gần nhất để replay cả khi client đổi key sau khi item đã được hủy.
            var cancelReceipt = order.OperationReceipts
                .Where(r => r.TargetItemId == item.Id && r.Action == RentalItem.CancelNotDeliveredAction && r.Status == RentalOperationReceipt.StatusSucceeded)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefault();

            // Giữ mốc hủy từ receipt cũ khi trả lại kết quả, thay vì biến thời điểm retry thành thời điểm hủy mới.
            DateTimeOffset cancelledAt = cancelReceipt?.CreatedAt ?? utcNow;
            // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
            if (cancelReceipt != null && !string.IsNullOrEmpty(cancelReceipt.ApiResponseJson))
            {
                // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                var cached = JsonSerializer.Deserialize<CancelNotDeliveredResponse>(cancelReceipt.ApiResponseJson, JsonOptions);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (cached != null) return cached;
            }
            return new CancelNotDeliveredResponse(item.Id, RentalItemStatus.Cancelled.ToString(), cancelledAt, item.RentalAmount + item.DepositAmount);
        }

        // 7. Dòng không ở AWAITING_HANDOVER -> 409 Order.CancellationForbidden
        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        // 8. Chưa qua mốc quá hạn giao -> 409 CANCEL_TOO_EARLY
        if (!HandoverDeadlinePolicy.IsNoticeDue(item, utcNow))
        {
            return RentalErrors.CancelTooEarly;
        }

        // Phải đối soát intent bàn giao còn treo trước khi hủy: Inventory có thể đã ghi
        // nhận giao máy nhưng kết quả chưa về. Hoàn tiền ngay sẽ sai dù local còn AwaitingHandover.
        // 9. Bằng chứng bàn giao đang được xử lý dở dang -> 409 HANDOVER_IN_PROGRESS
        bool handoverInProgress = order.OperationReceipts.Any(r =>
            r.TargetItemId == item.Id &&
            r.Action == "MarkAssetsHandedOver" &&
            r.Status is RentalOperationReceipt.StatusPending or RentalOperationReceipt.StatusProcessing or RentalOperationReceipt.StatusManualReconciliationRequired);

        // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
        if (handoverInProgress)
        {
            return RentalErrors.HandoverInProgress;
        }

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid actorId = caller.UserId!.Value;
        string cancelReason = string.IsNullOrWhiteSpace(reason) ? "Renter hủy do thiết bị không được bàn giao" : reason;
        decimal refundAmount = item.RentalAmount + item.DepositAmount;
        var response = new CancelNotDeliveredResponse(item.Id, RentalItemStatus.Cancelled.ToString(), utcNow, refundAmount);
        string responseJson = JsonSerializer.Serialize(response, JsonOptions);

        // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            RentalItem.CancelNotDeliveredAction,
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            targetItemId: item.Id,
            apiResponseJson: responseJson);

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // 10. Chuyển trạng thái dòng, rollup đơn hàng
            var cancelResult = item.ApplyNotDeliveredCancellation(actorId, ActorRole.Renter, utcNow, cancelReason, normalizedKey);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (cancelResult.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return cancelResult.Error;
            }

            // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
            RentalOrderStatus previousOrderStatus = order.Status;
            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);

            // Response hủy xác nhận quyết định tại Rental; refundAmount là số tiền cần hoàn,
            // không phải bằng chứng Payment đã chuyển tiền. Receipt theo dõi side effect tiếp theo.
            // 11. Lên lịch nhả allocation và hoàn tiền 100% trong transaction
            await _operationExecutor.ScheduleReleaseAndRefundAsync(order, item, actorId, cancelReason, utcNow, cancellationToken);

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(receipt);
            order.RecordReadModelUpdate(utcNow);

            // 12. Phát event RentalItemCancelled qua producer
            var itemCancelledRes = await _rentalEvents.EnqueueRentalItemCancelledAsync(
                new RentalItemCancelledPayload(order.Id, item.Id, actorId, ActorRole.Renter.ToString(), utcNow, KafkaContractSpec.ReasonCodes.HandoverNotDelivered, refundAmount, order.Version),
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

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Cancelled && order.Status == RentalOrderStatus.Cancelled)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderCancelledRes = await _rentalEvents.EnqueueRentalOrderCancelledAsync(
                    new RentalOrderCancelledPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.HandoverNotDelivered, order.OrderCode, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (orderCancelledRes.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderCancelledRes.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderCompletedRes = await _rentalEvents.EnqueueRentalOrderCompletedAsync(
                    new RentalOrderCompletedPayload(order.Id, order.RenterId, utcNow, order.Version),
                    correlationId: normalizedKey,
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

            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await _orderRepository.UpdateAsync(order, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (_notifier != null)
            {
                // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
                string changeType = order.Status == RentalOrderStatus.Cancelled
                    ? OrderChangeTypes.OrderCancelled
                    : OrderChangeTypes.ItemCancelled;

                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await _notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        changeType,
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
                            changeType,
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

    // Worker kết thúc item quá hạn bàn giao dưới khóa order; bảo vệ intent chưa rõ kết quả và lưu tác vụ bù trừ cùng thay đổi trạng thái.
    public async Task<Result<bool>> ExpireNoHandoverAsync(
        Guid itemId,
        string reason,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_OPERATION_KEY", "Operation key bắt buộc và không quá 64 ký tự.");
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        Guid? orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue)
        {
            return RentalErrors.ItemNotFound;
        }

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(15), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null)
        {
            return RentalErrors.ItemNotFound;
        }

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = operationKey.Trim();
        const string action = "HandoverDeadlineExpired";
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(new { Action = action, ItemId = item.Id });

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReceipt != null)
        {
            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (existingReceipt.Action == action && existingReceipt.Fingerprint == fingerprint &&
                existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
                return false;
            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa thao tác đã được dùng với nội dung khác.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status is RentalItemStatus.Expired or RentalItemStatus.Cancelled)
            return false;

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (!HandoverDeadlinePolicy.IsAutoExpireDue(item, utcNow, _autoExpireGraceHours))
            return Error.Conflict("HANDOVER_DEADLINE_NOT_REACHED", "Chưa đến hạn hệ thống kết thúc mục thuê chưa bàn giao.");

        // Tra cứu intent bàn giao còn Pending/Processing/cần đối soát; trạng thái local chưa đổi không chứng minh máy chưa được giao.
        bool handoverInProgress = order.OperationReceipts.Any(r =>
            r.TargetItemId == item.Id &&
            r.Action == "MarkAssetsHandedOver" &&
            r.Status is RentalOperationReceipt.StatusPending or RentalOperationReceipt.StatusProcessing or RentalOperationReceipt.StatusManualReconciliationRequired);

        // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
        if (handoverInProgress)
        {
            return RentalErrors.HandoverInProgress;
        }

        // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
        const string transitionAction = "HandoverDeadlineExpired";
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            action,
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            targetItemId: item.Id);

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            item.SetStatus(RentalItemStatus.Expired);
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), order.Id, item.Id, RentalItemStatus.AwaitingHandover.ToString(),
                RentalItemStatus.Expired.ToString(), transitionAction, null, ActorRole.System,
                reason, utcNow, normalizedKey));

            // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
            RentalOrderStatus previousOrderStatus = order.Status;
            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);

            // Lập tác vụ phụ thuộc cho luồng hiện tại; việc lập lịch chưa có nghĩa Inventory hoặc Payment đã thực hiện xong.
            await _operationExecutor.ScheduleReleaseAndRefundAsync(order, item, Guid.Empty, reason, utcNow, cancellationToken);

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(receipt);
            order.RecordReadModelUpdate(utcNow);

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var itemExpiredRes = await _rentalEvents.EnqueueRentalItemExpiredAsync(
                new RentalItemExpiredPayload(order.Id, item.Id, item.SupplierId, utcNow, KafkaContractSpec.ReasonCodes.HandoverExpired, order.Version),
                correlationId: normalizedKey,
                cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (itemExpiredRes.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return itemExpiredRes.Error;
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Expired && order.Status == RentalOrderStatus.Expired)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderExpiredRes = await _rentalEvents.EnqueueRentalOrderExpiredAsync(
                    new RentalOrderExpiredPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.HandoverExpired, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (orderExpiredRes.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderExpiredRes.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderCompletedRes = await _rentalEvents.EnqueueRentalOrderCompletedAsync(
                    new RentalOrderCompletedPayload(order.Id, order.RenterId, utcNow, order.Version),
                    correlationId: normalizedKey,
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

            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await _orderRepository.UpdateAsync(order, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (_notifier != null)
            {
                // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
                string changeType = order.Status == RentalOrderStatus.Expired
                    ? OrderChangeTypes.OrderCancelled
                    : OrderChangeTypes.HandoverExpired;

                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await _notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        changeType,
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
                            changeType,
                            utcNow),
                        cancellationToken);
                }
            }
            return true;
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
