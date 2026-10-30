using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.RentalService.Application.Services.Checkout;

/// <remarks>
/// Điểm điều phối checkout: khóa renter → kiểm tra idempotency → chuẩn bị snapshot → lưu operation → giữ lịch qua HTTP Inventory.
/// Khi đủ các dòng: lưu order, assignments, Outbox RentalOrderCreated và receipt tạo invoice trong cùng transaction.
/// Sau commit mới gọi HTTP Payment và thông báo realtime. Operation tạo đơn thành công vẫn có thể đang chờ invoice.
/// Khi giữ lịch thất bại dứt khoát, xếp lệnh ReleaseAllocation vào Outbox để bù trừ; chưa coi là đã nhả máy.
/// </remarks>
public sealed class CheckoutService : ICheckoutService
{
    private static readonly TimeSpan BudgetDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ICheckoutPreparationService _preparation;
    private readonly IRentalCheckoutOperationRepository _operationRepository;
    private readonly IRentalOrderRepository _orders;
    private readonly IOrderLockManager _locks;
    private readonly IInventoryRentalClient _inventoryClient;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly ICheckoutOrderWriter _orderWriter;
    private readonly IRentalEventProducer _rentalEvents;
    private readonly ICheckoutInvoiceIssuer _invoiceIssuer;
    private readonly IPaymentRentalClient? _paymentClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckoutService> _logger;
    private readonly IOrderHubNotifier? _notifier;
    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public CheckoutService(
        ICheckoutPreparationService preparation,
        IRentalCheckoutOperationRepository operationRepository,
        IRentalOrderRepository orders,
        IOrderLockManager locks,
        IInventoryRentalClient inventoryClient,
        IInventoryCommandProducer inventoryCommands,
        ICheckoutOrderWriter orderWriter,
        IRentalEventProducer rentalEvents,
        ICheckoutInvoiceIssuer invoiceIssuer,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null,
        IPaymentRentalClient? paymentClient = null,
        ILogger<CheckoutService>? logger = null,
        IOrderHubNotifier? notifier = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _operationRepository = operationRepository ?? throw new ArgumentNullException(nameof(operationRepository));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _inventoryClient = inventoryClient ?? throw new ArgumentNullException(nameof(inventoryClient));
        _inventoryCommands = inventoryCommands ?? throw new ArgumentNullException(nameof(inventoryCommands));
        _orderWriter = orderWriter ?? throw new ArgumentNullException(nameof(orderWriter));
        _rentalEvents = rentalEvents ?? throw new ArgumentNullException(nameof(rentalEvents));
        _invoiceIssuer = invoiceIssuer ?? throw new ArgumentNullException(nameof(invoiceIssuer));
        _paymentClient = paymentClient;
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CheckoutService>.Instance;
        _notifier = notifier;
    }

    // Điểm vào gửi đơn: cùng renter + idempotency key phải tiếp tục/replay operation cũ.
    // Kiểm tra replay trước khi đọc giỏ vì lần chạy trước có thể đã tạo đơn hoặc thay đổi giỏ.
    public async Task<CheckoutSubmitResult> SubmitAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(renter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (renter.RenterId == Guid.Empty)
        {
            return CheckoutErrors.Unauthenticated;
        }

        // OperationId nhận diện một lần checkout; fingerprint nhận diện nội dung của lần đó.
        // Cùng key nhưng khác payload phải bị từ chối, tránh vô tình trả đơn của yêu cầu cũ.
        string operationId = CheckoutRules.DeriveOperationId(renter.RenterId, idempotencyKey);
        string fingerprint = ComputeRequestFingerprint(renter.RenterId, request);

        // Hạn xử lý HTTP chỉ giới hạn thời gian chờ của request; không phải hạn sống của checkout.
        // Hết budget trả trạng thái chưa sẵn sàng, để client/worker tiếp tục operation đã lưu.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(BudgetDuration);
        var budgetToken = budgetCts.Token;

        // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
        try
        {
            // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
            IAsyncDisposable renterLock;
            // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
            try
            {
                // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
                renterLock = await _locks.AcquireRenterLockAsync(renter.RenterId, LockTimeout, budgetToken);
            }
            // Không lấy được khóa trong thời gian cho phép: trả trạng thái bận/chưa sẵn sàng để caller thử lại thay vì ghi đồng thời.
            catch (TimeoutException)
            {
                return CheckoutErrors.InProgress;
            }

            // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
            await using (renterLock)
            {
                // Replay phải đi trước đọc giỏ: lần thành công trước có thể đã xóa item khỏi giỏ.
                // Đọc giỏ trước sẽ biến retry hợp lệ thành lỗi giỏ rỗng hoặc tạo snapshot khác.
                var existingOp = await _operationRepository.GetByOperationIdAsync(operationId, budgetToken);
                // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
                if (existingOp is not null)
                {
                    // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
                    if (existingOp.RenterUserId != renter.RenterId || existingOp.RequestFingerprint != fingerprint)
                    {
                        return CheckoutErrors.IdempotencyKeyReused;
                    }

                    // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
                    IAsyncDisposable existingOrderLock;
                    // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
                    try
                    {
                        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
                        existingOrderLock = await _locks.AcquireOrderLockAsync(existingOp.ExpectedRentalOrderId, LockTimeout, budgetToken);
                    }
                    // Không lấy được khóa trong thời gian cho phép: trả trạng thái bận/chưa sẵn sàng để caller thử lại thay vì ghi đồng thời.
                    catch (TimeoutException)
                    {
                        return CheckoutErrors.InProgress;
                    }

                    // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
                    await using (existingOrderLock)
                    {
                        return await ContinueOrReplayOperationAsync(existingOp, isReplay: true, budgetToken);
                    }
                }

                // Chặn hai thao tác đang chạy, không chặn renter có nhiều đơn PendingPayment.
                // Khóa renter bao quanh cả kiểm tra và tạo operation để tránh hai request cùng lọt qua.
                var activeOp = await _operationRepository.GetActiveOperationByRenterAsync(renter.RenterId, budgetToken);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (activeOp is not null)
                {
                    return Error.Conflict("ACTIVE_CHECKOUT_IN_PROGRESS", "Bạn đang có một thao tác gửi đơn đang được xử lý.");
                }

                // 3. Chuẩn bị checkout (snapshot, bảng giá, giỏ hàng)
                var prepared = await _preparation.PrepareAsync(renter, request, budgetToken);
                // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
                if (prepared.IsFailure)
                {
                    return prepared;
                }

                // Giữ nguyên kế hoạch checkout vừa được xác thực để các bước hold và lưu đơn dùng cùng snapshot giá/ngày.
                var plan = prepared.Plan;
                var expectedOrderId = Guid.NewGuid();
                var utcNow = _timeProvider.GetUtcNow();
                var holdExpiresAt = utcNow.Add(CheckoutRules.PaymentWindow);

                // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
                IAsyncDisposable orderLock;
                // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
                try
                {
                    // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
                    orderLock = await _locks.AcquireOrderLockAsync(expectedOrderId, LockTimeout, budgetToken);
                }
                // Không lấy được khóa trong thời gian cho phép: trả trạng thái bận/chưa sẵn sàng để caller thử lại thay vì ghi đồng thời.
                catch (TimeoutException)
                {
                    return CheckoutErrors.InProgress;
                }

                // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
                await using (orderLock)
                {
                    // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
                    var lineStatuses = plan.Lines.Select(line =>
                    {
                        // Cấp ID cho bản ghi mới của lần xử lý; ID được đưa vào journal/snapshot để các bước tiếp tục tham chiếu đúng đối tượng.
                        var rentalItemId = Guid.NewGuid();
                        var opKey = $"rental-item:{rentalItemId}";
                        return new CheckoutItemHoldDescriptor(
                            CartItemId: line.CartItemId,
                            RentalItemId: rentalItemId,
                            EquipmentListingId: line.EquipmentListingId,
                            OperationKey: opKey,
                            Status: "Pending");
                    }).ToList();

                    // Lưu operation, fingerprint, order/item ID, snapshot và deadline trước lời gọi HTTP giữ chỗ đầu tiên.
                    // Lần tiếp tục dùng lại các định danh này để Inventory nhận biết cùng yêu cầu.
                    var checkoutOp = new RentalCheckoutOperation(
                        operationId: operationId,
                        renterUserId: renter.RenterId,
                        requestFingerprint: fingerprint,
                        expectedRentalOrderId: expectedOrderId,
                        holdExpiresAt: holdExpiresAt,
                        preparationSnapshotJson: JsonSerializer.Serialize(plan, JsonOptions),
                        itemHoldStatusesJson: JsonSerializer.Serialize(lineStatuses, JsonOptions),
                        createdAt: utcNow);

                    // Lưu journal checkout trước side effect giữ máy đầu tiên để worker có dữ liệu tiếp tục nếu request bị ngắt.
                    await _operationRepository.AddAsync(checkoutOp, budgetToken);
                    await _unitOfWork.SaveChangesAsync(budgetToken);

                    return await ExecuteOrchestrationAsync(checkoutOp, plan, lineStatuses, isReplay: false, budgetToken);
                }
            }
        }
        // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckoutResult.CheckoutNotReady(operationId);
        }
    }

    // Tiếp tục operation đã lưu: lấy khóa theo cùng thứ tự renter rồi order, đọc lại và chạy phần còn thiếu.
    // Không tạo checkout mới hoặc đổi deadline để xử lý lại một lần gửi đơn.
    public async Task<CheckoutSubmitResult> ContinueOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var op = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (op is null)
        {
            return Error.NotFound("CHECKOUT_OPERATION_NOT_FOUND", "Không tìm thấy thao tác gửi đơn.");
        }

        // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
        IAsyncDisposable renterLock;
        // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
        try
        {
            // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
            renterLock = await _locks.AcquireRenterLockAsync(op.RenterUserId, LockTimeout, cancellationToken);
        }
        // Không lấy được khóa trong thời gian cho phép: trả trạng thái bận/chưa sẵn sàng để caller thử lại thay vì ghi đồng thời.
        catch (TimeoutException)
        {
            return CheckoutResult.CheckoutNotReady(operationId);
        }

        // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
        await using (renterLock)
        await using (var orderLock = await _locks.AcquireOrderLockAsync(op.ExpectedRentalOrderId, LockTimeout, cancellationToken))
        {
            // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
            var currentOp = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (currentOp is null)
            {
                return Error.NotFound("CHECKOUT_OPERATION_NOT_FOUND", "Không tìm thấy thao tác gửi đơn.");
            }

            return await ContinueOrReplayOperationAsync(currentOp, isReplay: false, cancellationToken);
        }
    }

    // Chọn nhánh theo journal/order hiện tại: trả lỗi cuối, dùng order đã tạo, bù trừ quá hạn hoặc tiếp tục các bước còn thiếu.
    private async Task<CheckoutSubmitResult> ContinueOrReplayOperationAsync(
        RentalCheckoutOperation op,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var utcNow = _timeProvider.GetUtcNow();

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (op.Status == CheckoutOperationStatus.Failed)
        {
            return CheckoutResult.Failure(Error.Conflict(op.ErrorCode ?? "FAILED", op.ErrorMessage ?? "Thao tác gửi đơn thất bại."));
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (op.Status == CheckoutOperationStatus.TimedOut)
        {
            return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orders.GetByIdWithDetailsAsync(op.ExpectedRentalOrderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is not null)
        {
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (order.Status is RentalOrderStatus.Expired or RentalOrderStatus.Cancelled)
            {
                return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_" + order.Status.ToString().ToUpperInvariant(), $"Đơn hàng {order.OrderCode} ở trạng thái {order.Status}."));
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (order.Status == RentalOrderStatus.Paid)
            {
                // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
                if (order.TotalPayableAmount == 0)
                {
                    return CheckoutResponse.From(order, isReplay, payment: null);
                }
                return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_PAID", $"Đơn hàng {order.OrderCode} đã được thanh toán."));
            }

            // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
            if (utcNow >= order.PaymentDeadline)
            {
                return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
            }


            return await EnsurePaymentAndQrAsync(op, order, isReplay, cancellationToken);
        }

        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (utcNow >= op.HoldExpiresAt)
        {
            // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
            await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                op.MarkTimedOut(utcNow);
                var lineDescs = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions) ?? [];
                var heldItemIds = lineDescs.Where(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)).Select(l => l.RentalItemId);
                await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                await _operationRepository.UpdateAsync(op, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await failTx.CommitAsync(cancellationToken);
            }
            return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
        }

        // Tiếp tục từ snapshot và ID đã lưu, không tính lại giá/đổi máy theo giỏ hiện tại.
        // Nếu làm mới kế hoạch ở đây, retry có thể tạo thêm hold hoặc đổi điều kiện thương mại.
        var plan = JsonSerializer.Deserialize<CheckoutPlan>(op.PreparationSnapshotJson, JsonOptions);
        var lineStatuses = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions);
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (plan is null || lineStatuses is null)
        {
            return Error.Failure("CHECKOUT_CORRUPTED", "Dữ liệu checkout operation bị hỏng.");
        }

        return await ExecuteOrchestrationAsync(op, plan, lineStatuses, isReplay, cancellationToken);
    }

    // Hold đủ mọi dòng trước khi tạo order; lỗi nghiệp vụ ghi bù trừ, lỗi chưa rõ kết quả giữ journal cho lần tiếp tục.
    private async Task<CheckoutSubmitResult> ExecuteOrchestrationAsync(
        RentalCheckoutOperation op,
        CheckoutPlan plan,
        List<CheckoutItemHoldDescriptor> lineStatuses,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var utcNow = _timeProvider.GetUtcNow();

        // 1. Gọi HTTP Inventory giữ từng dòng tuần tự, bỏ qua dòng đã Held trong operation.
        // Không giữ transaction database trong lúc chờ mạng; kết quả mỗi dòng được lưu để tiếp tục sau gián đoạn.
        for (int i = 0; i < plan.Lines.Count; i++)
        {
            // Chọn dòng kế hoạch cùng descriptor đã lưu để gửi hold đúng ID và duy trì tiến độ từng dòng.
            var line = plan.Lines[i];
            var desc = lineStatuses.FirstOrDefault(l => l.CartItemId == line.CartItemId)
                ?? lineStatuses[i];

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (string.Equals(desc.Status, "Held", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
            if (utcNow >= op.HoldExpiresAt)
            {
                return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
            }

            // Chuyển khoảng ngày từ kế hoạch sang contract Inventory bằng adapter; không tự cộng/trừ ngày theo cách riêng trong orchestration.
            var (holdStartDate, holdEndDate) = RentalInventoryDateAdapter
                .ToInventoryDates(line.ExpectedStartDate, line.ExpectedEndDate).Value;

            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var holdRequest = new CreateHoldRequest(
                ListingId: line.EquipmentListingId,
                Quantity: line.Quantity,
                StartDate: holdStartDate,
                EndDate: holdEndDate,
                HoldExpiresAt: op.HoldExpiresAt,
                SourceSystem: InventoryContractSpec.SourceSystem,
                GroupReference: $"rental-item:{desc.RentalItemId}",
                Notes: $"Order checkout: item {desc.RentalItemId}");

            // Mất phản hồi HTTP không chứng minh hold thất bại: Inventory có thể đã commit.
            // Giữ nguyên GroupReference để lần gọi lại nhận cùng nhóm allocation, tránh giữ trùng.
            Result<CreateHoldResponse> holdResult;
            // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
            try
            {
                // Gọi service sở hữu dữ liệu/tác vụ bằng contract đã chuẩn bị; kết quả phải được kiểm tra trước khi chốt trạng thái local.
                holdResult = await _inventoryClient.CreateHoldAsync(holdRequest, cancellationToken);
            }
            // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CheckoutResult.CheckoutNotReady(op.OperationId);
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch (Exception ex)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogWarning(ex, "Lỗi gọi HTTP hold cho item {RentalItemId}", desc.RentalItemId);
                return CheckoutResult.CheckoutNotReady(op.OperationId);
            }

            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (holdResult.IsFailure)
            {
                // Lỗi tạm thời giữ cơ hội retry/tiếp tục bằng journal cũ; không chốt thất bại nghiệp vụ từ lỗi mạng.
                if (IsTemporary(holdResult.Error))
                {
                    return CheckoutResult.CheckoutNotReady(op.OperationId);
                }

                // Lỗi nghiệp vụ kết thúc checkout: phải ghi lệnh nhả các hold đã thành công.
                // Đây là bù trừ giữa hai service, không phải rollback transaction xuyên database.
                await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
                {
                    // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                    op.MarkFailed(holdResult.Error.Code, holdResult.Error.Message, _timeProvider.GetUtcNow());
                    var heldItemIds = lineStatuses.Where(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)).Select(l => l.RentalItemId);
                    await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                    await _operationRepository.UpdateAsync(op, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await failTx.CommitAsync(cancellationToken);
                }

                // Đóng gói xung đột hold theo CartItemId/listing/số lượng để client chỉ ra chính xác dòng không thể checkout.
                var conflicts = new List<CheckoutHoldConflict>
                {
                    new(line.CartItemId, line.EquipmentListingId, line.Quantity,
                        CheckoutHoldConflictCode.InsufficientAvailability, holdResult.Error.Message)
                };
                return CheckoutResult.HoldFailure(holdResult.Error, conflicts);
            }

            // HTTP thành công chưa đủ: đối chiếu listing, số lượng và trạng thái trước khi
            // dùng allocation để tạo đơn. Không nhận một phần số máy cho checkout all-or-nothing.
            var holdResp = holdResult.Value;
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (holdResp.ListingId != line.EquipmentListingId ||
                holdResp.Quantity != line.Quantity ||
                holdResp.Allocations == null ||
                holdResp.Allocations.Count != line.Quantity ||
                holdResp.Allocations.Any(a => !string.Equals(a.Status, "Held", StringComparison.OrdinalIgnoreCase)))
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogError("Inventory hold response không khớp dữ liệu cho line {RentalItemId}", desc.RentalItemId);
                await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
                {
                    // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                    op.MarkFailed("INVALID_HOLD_RESPONSE", "Inventory trả dữ liệu giữ chỗ không hợp lệ.", _timeProvider.GetUtcNow());
                    var heldItemIds = lineStatuses.Where(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)).Select(l => l.RentalItemId);
                    await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                    await _operationRepository.UpdateAsync(op, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await failTx.CommitAsync(cancellationToken);
                }
                return CheckoutResult.Failure(Error.Conflict("INVALID_HOLD_RESPONSE", "Inventory trả dữ liệu giữ chỗ không hợp lệ."));
            }

            // Lưu mapping allocation thực tế do Inventory cấp vào tiến độ hold; Rental không tự chọn AssetId để thay thế.
            var allocMappings = holdResp.Allocations
                .Select(a => new AllocationAssetMappingPayload(a.Id, a.AssetId, string.Empty))
                .ToList();

            // Ghi tiến độ Held cùng allocation thực tế vào descriptor; lần tiếp tục sẽ bỏ qua dòng đã được giữ thành công.
            var updatedDesc = desc with
            {
                Status = "Held",
                Allocations = allocMappings
            };
            int descIdx = lineStatuses.FindIndex(l => l.RentalItemId == desc.RentalItemId);
            lineStatuses[descIdx] = updatedDesc;

            // Đóng gói snapshot/payload đang xử lý để lưu hoặc truyền theo contract; giữ dữ liệu của lần quyết định hiện tại.
            op.UpdateItemHoldStatuses(JsonSerializer.Serialize(lineStatuses, JsonOptions), _timeProvider.GetUtcNow());
            await _operationRepository.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // 2. Xác nhận tất cả dòng đã giữ thành công
        if (!lineStatuses.All(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)))
        {
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }

        // 3. Lưu order, assignments, event Outbox và receipt invoice
        var order = await _orders.GetByIdWithDetailsAsync(op.ExpectedRentalOrderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null)
        {
            // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
            var heldLines = plan.Lines.Select(line =>
            {
                // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                var desc = lineStatuses.First(l => l.CartItemId == line.CartItemId);
                var assignedAssets = desc.Allocations!
                    .Select(a => new AssignedAssetDto(a.AllocationId, a.AssetId, "Held"))
                    .ToList();
                return new CheckoutHeldLine(line, desc.RentalItemId, desc.OperationKey, assignedAssets);
            }).ToList();

            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            DateTimeOffset orderCreatedAt = _timeProvider.GetUtcNow();
            await using var createTx = await _unitOfWork.BeginTransactionAsync(cancellationToken);

            // Chuyển kế hoạch và mapping đã hold cho writer trong transaction tạo order; giữ deadline và order ID đã lưu ở journal.
            var orderResult = await _orderWriter.CreateOrderAsync(
                plan,
                heldLines,
                op.OperationId,
                op.ExpectedRentalOrderId,
                op.HoldExpiresAt,
                orderCreatedAt,
                cancellationToken);

            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (orderResult.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await createTx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();

                // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
                await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
                {
                    // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                    op.MarkFailed(orderResult.Error.Code, orderResult.Error.Message, _timeProvider.GetUtcNow());
                    var heldItemIds = lineStatuses.Select(l => l.RentalItemId);
                    await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                    await _operationRepository.UpdateAsync(op, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await failTx.CommitAsync(cancellationToken);
                }

                return CheckoutResult.Failure(orderResult.Error);
            }

            // Từ đây dùng chính order writer trả về để tạo event và receipt invoice, tránh dựng lại một aggregate khác.
            order = orderResult.Value;

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var enqueueEventRes = await _rentalEvents.EnqueueRentalOrderCreatedAsync(
                new RentalOrderCreatedPayload(
                    order.Id,
                    order.OrderCode,
                    order.RenterId,
                    order.Currency,
                    order.TotalRentalAmount,
                    order.TotalDepositAmount,
                    order.TotalPayableAmount,
                    order.PaymentDeadline,
                    order.CreatedAt,
                    order.Items.Select(item => new RentalOrderCreatedItemPayload(
                        item.Id,
                        item.EquipmentListingId,
                        item.SupplierId,
                        item.Quantity,
                        item.ExpectedStartDate,
                        item.ExpectedEndDate,
                        item.RentalAmount,
                        item.DepositAmount,
                        item.EquipmentTypeId,
                        item.EquipmentTypeNameSnapshot,
                        item.BrandId,
                        item.BrandNameSnapshot,
                        item.ModelSnapshot)).ToArray(),
                    order.Version),
                correlationId: order.Id.ToString("D"),
                cancellationToken: cancellationToken);

            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (enqueueEventRes.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await createTx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return CheckoutResult.Failure(enqueueEventRes.Error);
            }

            // Tạo receipt hóa đơn cùng transaction với order: nếu process dừng ngay sau commit,
            // recovery worker vẫn biết cần gọi Payment. Chưa có invoice không đồng nghĩa tạo đơn lỗi.
            string invoiceOpKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
            var invoiceReceipt = RentalOperationReceipt.CreatePending(
                Guid.NewGuid(),
                order.Id,
                invoiceOpKey,
                "CreateInvoice",
                RentalOperationExecutor.ComputeFingerprint($"CreateInvoice:{order.Id}"),
                orderCreatedAt,
                targetItemId: null,
                payloadJson: null);

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(invoiceReceipt);
            op.MarkSucceeded(orderCreatedAt, invoiceStatus: "Pending");

            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await _orders.UpdateAsync(order, cancellationToken);
            await _operationRepository.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await createTx.CommitAsync(cancellationToken);

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
                        OrderChangeTypes.OrderCreated,
                        orderCreatedAt),
                    cancellationToken);
            }
        }

        // 4. HTTP Payment: tạo invoice, nhận cả qrUrl (commit trước khi gọi Payment; không giữ db tx)
        return await EnsurePaymentAndQrAsync(op, order, isReplay, cancellationToken);
    }

    // Đảm bảo thông tin hóa đơn để trả QR; tách đơn tổng tiền 0 và các trạng thái hóa đơn đã kết thúc khỏi nhánh chờ Payment.
    private async Task<CheckoutSubmitResult> EnsurePaymentAndQrAsync(
        RentalCheckoutOperation op,
        RentalOrder order,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var utcNow = _timeProvider.GetUtcNow();

        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (order.TotalPayableAmount == 0)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var zeroInvoiceRes = await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
            // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
            if (zeroInvoiceRes.IsSuccess)
            {
                // Dùng hóa đơn đã được issuer kiểm tra để chọn response thanh toán hoặc nhánh đơn tổng tiền bằng 0.
                var zeroInv = zeroInvoiceRes.Value;
                // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                if (order.Status == RentalOrderStatus.PendingPayment)
                {
                    // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                    var markPaidRes = order.MarkAsPaid(zeroInv.InvoiceRef, zeroInv.InvoiceRef, utcNow, utcNow);
                    // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
                    if (markPaidRes.IsSuccess)
                    {
                        // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
                        order.RecordReadModelUpdate(utcNow);
                        await _orders.UpdateAsync(order, cancellationToken);
                        await _unitOfWork.SaveChangesAsync(cancellationToken);
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
                                    OrderChangeTypes.PaymentConfirmed,
                                    utcNow),
                                cancellationToken);

                            // Xử lý riêng từng đơn con thuộc order; không trộn thông tin hoặc thông báo giữa các supplier.
                            foreach (var subOrder in order.SubOrders)
                            {
                                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                                await _notifier.NotifySupplierSubOrderChangedAsync(
                                    subOrder.SupplierId,
                                    new SupplierSubOrderChangedNotification(
                                        Guid.NewGuid(),
                                        order.Id,
                                        subOrder.Id,
                                        order.Version,
                                        OrderChangeTypes.PaymentConfirmed,
                                        utcNow),
                                cancellationToken);
                            }
                        }
                    }
                }
            }
            return CheckoutResponse.From(order, isReplay, payment: null);
        }

        // Giữ kết quả gọi invoice để tách lỗi tạm thời, phản hồi chưa sẵn sàng và các trạng thái thanh toán đã kết thúc.
        Result<CreateInvoiceResponse> invoiceResult;
        // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
        try
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            invoiceResult = await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
        }
        // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }
        // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
        catch (Exception ex)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogWarning(ex, "Lỗi gọi payment cho order {OrderId}", order.Id);
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (invoiceResult.IsFailure)
        {
            // Lỗi tạm thời giữ cơ hội retry/tiếp tục bằng journal cũ; không chốt thất bại nghiệp vụ từ lỗi mạng.
            if (IsTemporary(invoiceResult.Error))
            {
                return CheckoutResult.CheckoutNotReady(op.OperationId);
            }
            return CheckoutResult.Failure(invoiceResult.Error);
        }

        // Dùng hóa đơn đã được issuer kiểm tra để chọn response thanh toán hoặc nhánh đơn tổng tiền bằng 0.
        var invoice = invoiceResult.Value;

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase))
        {
            return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_PAID", $"Đơn hàng {order.OrderCode} đã được thanh toán."));
        }
        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (string.Equals(invoice.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(invoice.Status, "Expired", StringComparison.OrdinalIgnoreCase) ||
            utcNow >= order.PaymentDeadline)
        {
            return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_" + invoice.Status.ToUpperInvariant(), $"Đơn hàng {order.OrderCode} ở trạng thái {invoice.Status}."));
        }

        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(invoice.QrUrl))
        {
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }

        // Dựng response/thông báo từ dữ liệu vừa xử lý; không tự thay đổi trạng thái nghiệp vụ trong bước ánh xạ.
        var paymentInfo = new CheckoutPaymentInfoDto(
            InvoiceRef: invoice.InvoiceRef,
            Status: invoice.Status,
            QrUrl: invoice.QrUrl,
            Bank: invoice.Bank ?? string.Empty,
            Account: invoice.Account ?? string.Empty,
            TransferContent: invoice.TransferContent ?? string.Empty);

        return CheckoutResponse.From(order, isReplay, paymentInfo);
    }

    // Điểm polling của client: kiểm tra quyền renter trước khi trả trạng thái.
    // Operation Succeeded là đã tạo được order; ReadyForPayment còn phụ thuộc invoice và deadline.
    public async Task<Result<CheckoutOperationStatusResponse>> GetOperationStatusAsync(
        string operationId,
        Guid renterUserId,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var op = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (op is null || op.RenterUserId != renterUserId)
        {
            return Error.NotFound("CHECKOUT_OPERATION_NOT_FOUND", "Không tìm thấy thao tác gửi đơn.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (op.Status == CheckoutOperationStatus.Succeeded)
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var order = await _orders.GetForCheckoutPollingAsync(op.ExpectedRentalOrderId, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order != null && order.RenterId != renterUserId)
            {
                // Ẩn order không thuộc renter khỏi response polling, không trả dữ liệu chỉ vì operation trỏ tới ID đó.
                order = null;
            }

            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order is null)
            {
                return new CheckoutOperationStatusResponse(
                    OperationId: op.OperationId,
                    Status: op.Status.ToString(),
                    OrderId: null,
                    OrderCode: null,
                    Order: null,
                    InvoiceStatus: null,
                    ReadyForPayment: false);
            }

            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            var now = _timeProvider.GetUtcNow();
            bool hasValidInvoice = !string.IsNullOrWhiteSpace(order.InvoiceRef);
            bool readyForPayment = order.Status == RentalOrderStatus.PendingPayment
                && hasValidInvoice
                && now < order.PaymentDeadline;

            // Dựng trạng thái hóa đơn cho polling độc lập với trạng thái checkout; order tồn tại chưa đủ để ReadyForPayment.
            string invoiceStatus;
            CheckoutPaymentInfoDto? paymentInfo = null;
            // Có InvoiceRef thì trả metadata hóa đơn; điều kiện còn hạn/thanh toán được đã được tính riêng.
            if (hasValidInvoice)
            {
                // Polling ghi nhận đã có mã invoice; các trường QR/bank rỗng ở đây không tự tạo dữ liệu thanh toán thay Payment.
                invoiceStatus = "Ready";
                paymentInfo = new CheckoutPaymentInfoDto(
                    order.InvoiceRef!,
                    "Ready",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }
            else
            {
                // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
                string invoiceOpKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
                var invoiceReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == invoiceOpKey);

                // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
                if (order.Status is not RentalOrderStatus.PendingPayment || now >= order.PaymentDeadline)
                {
                    // Đánh dấu hóa đơn không thể tiếp tục phục vụ thanh toán theo trạng thái order/deadline hoặc lỗi nghiệp vụ của receipt.
                    invoiceStatus = "Failed";
                }
                else if (invoiceReceipt?.Status == RentalOperationReceipt.StatusFailed &&
                         invoiceReceipt.ErrorClassification == RentalOperationReceipt.ClassificationBusiness)
                {
                    // Đánh dấu hóa đơn không thể tiếp tục phục vụ thanh toán theo trạng thái order/deadline hoặc lỗi nghiệp vụ của receipt.
                    invoiceStatus = "Failed";
                }
                else
                {
                    // Chưa có invoice hoàn chỉnh nhưng chưa kết thúc lỗi nghiệp vụ; client tiếp tục chờ/poll thay vì tạo checkout mới.
                    invoiceStatus = "Pending";
                }
            }

            // Dùng mapper checkout chung để kết quả polling và submit có cùng cách biểu diễn order.
            var orderDto = CheckoutResponse.From(order, false, paymentInfo);
            return new CheckoutOperationStatusResponse(
                OperationId: op.OperationId,
                Status: op.Status.ToString(),
                OrderId: order.Id,
                OrderCode: order.OrderCode,
                Order: orderDto,
                InvoiceStatus: invoiceStatus,
                ReadyForPayment: readyForPayment);
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (op.Status == CheckoutOperationStatus.Failed)
        {
            return new CheckoutOperationStatusResponse(
                OperationId: op.OperationId,
                Status: op.Status.ToString(),
                ErrorCode: op.ErrorCode ?? "FAILED",
                ErrorMessage: op.ErrorMessage ?? "Thao tác gửi đơn thất bại.",
                ReadyForPayment: false);
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (op.Status == CheckoutOperationStatus.TimedOut)
        {
            return new CheckoutOperationStatusResponse(
                OperationId: op.OperationId,
                Status: op.Status.ToString(),
                ErrorCode: "TIMEOUT",
                ErrorMessage: "Hết hạn giữ lịch cho đơn hàng.",
                ReadyForPayment: false);
        }

        return new CheckoutOperationStatusResponse(
            OperationId: op.OperationId,
            Status: op.Status.ToString(),
            ReadyForPayment: false);
    }

    // Bù trừ khi checkout không hoàn tất: ghi ReleaseAllocation vào Outbox trong transaction của caller.
    // Khóa lệnh ổn định giúp retry không tạo thêm tác vụ release; Inventory sẽ xác nhận kết quả về sau.
    private async Task<Result> StageCheckoutReleasesAsync(
        Guid orderId,
        IEnumerable<Guid> rentalItemIds,
        CancellationToken ct)
    {
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (Guid itemId in rentalItemIds.Distinct().Order())
        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var release = new ReleaseAllocationCommandPayload(
                RentalOrderId: orderId,
                RentalItemId: itemId,
                OperationKey: $"checkout:compensation:{orderId:N}:{itemId:N}:release",
                Reason: "Checkout did not complete",
                AllocationGroupId: $"rental-item:{itemId}");

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var result = await _inventoryCommands.EnqueueReleaseAllocationAsync(
                release,
                correlationId: orderId.ToString("D"),
                cancellationToken: ct);

            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (result.IsFailure) return result.Error;
        }

        return Result.Success();
    }

    // Nhận diện lỗi phụ thuộc cần tiếp tục operation thay vì đánh dấu checkout thất bại nghiệp vụ.
    private static bool IsTemporary(Error error) =>
        error.Type is ErrorType.ServiceUnavailable or ErrorType.Failure;

    // Băm cả renter và nội dung request để phát hiện cùng Idempotency-Key nhưng đổi payload checkout.
    private static string ComputeRequestFingerprint(Guid renterId, CheckoutRequest request)
    {
        // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
        var wrapper = new { RenterId = renterId, Request = request };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wrapper, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public sealed record CheckoutItemHoldDescriptor(
    Guid CartItemId,
    Guid RentalItemId,
    Guid EquipmentListingId,
    string OperationKey,
    string Status,
    IReadOnlyList<AllocationAssetMappingPayload>? Allocations = null);
