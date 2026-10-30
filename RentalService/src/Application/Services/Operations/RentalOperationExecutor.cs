#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Hubs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Checkout;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Thực thi side effect dựa trên RentalOperationReceipt: khóa order → đọc lại receipt → kiểm tra trạng thái/lịch retry
/// và tác vụ tiên quyết → gọi nhánh xử lý tương ứng → lưu kết quả để retry/replay dùng cùng định danh.
/// Các file partial là các phần của cùng executor. Receipt là nhật ký tác vụ, khác Inbox nhận sự kiện và Outbox gửi bản tin.
/// </remarks>
public sealed partial class RentalOperationExecutor : IRentalOperationExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOperationReceiptRepository _receiptRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IInventoryRentalClient _inventoryClient;
    private readonly IPaymentRentalClient _paymentClient;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly ICheckoutInvoiceIssuer? _invoiceIssuer;
    private readonly IRentalCheckoutOperationRepository? _checkoutOperationRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalOperationExecutor> _logger;
    private readonly string _rentalEventsTopic;
    private readonly IRentalEventProducer _rentalEvents;
    private readonly IOrderHubNotifier? _notifier;
    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public RentalOperationExecutor(
        IRentalOrderRepository orderRepository,
        IOperationReceiptRepository receiptRepository,
        IOutboxRepository outboxRepository,
        IOrderLockManager lockManager,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient,
        IInventoryCommandProducer inventoryCommands,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<RentalOperationExecutor> logger,
        ICheckoutInvoiceIssuer? invoiceIssuer = null,
        IRentalCheckoutOperationRepository? checkoutOperationRepo = null,
        TimeProvider? timeProvider = null,
        IRentalEventProducer? rentalEvents = null,
        IOrderHubNotifier? notifier = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _receiptRepository = receiptRepository ?? throw new ArgumentNullException(nameof(receiptRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _inventoryClient = inventoryClient ?? throw new ArgumentNullException(nameof(inventoryClient));
        _paymentClient = paymentClient ?? throw new ArgumentNullException(nameof(paymentClient));
        _inventoryCommands = inventoryCommands!;
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
        _invoiceIssuer = invoiceIssuer;
        _checkoutOperationRepo = checkoutOperationRepo;
        _rentalEvents = rentalEvents ?? new Dprental.RentalService.Application.Kafka.Producers.RentalEventProducer(
            outboxRepository,
            Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions { RentalEventsTopic = _rentalEventsTopic }),
            _timeProvider);
        _notifier = notifier;
    }

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public RentalOperationExecutor(
        IRentalOrderRepository orderRepository,
        IOperationReceiptRepository receiptRepository,
        IOutboxRepository outboxRepository,
        IOrderLockManager lockManager,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<RentalOperationExecutor> logger,
        TimeProvider? timeProvider = null)
        : this(
            orderRepository,
            receiptRepository,
            outboxRepository,
            lockManager,
            inventoryClient,
            paymentClient,
            new InventoryCommandProducer(outboxRepository, Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions { Enabled = true })),
            unitOfWork,
            configuration,
            logger,
            null,
            null,
            timeProvider)
    {
    }

    // Băm action và định danh logic thành key ổn định để cùng tác vụ dùng lại được qua API, worker và provider.
    public static string ComputeOperationKey(string action, string logicalIdentity)
    {
        // Tạo fingerprint nội dung để nhận diện payload đã lưu; không dùng fingerprint thay cho kiểm tra quyền sở hữu.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{action}:{logicalIdentity}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    // Băm nội dung đã chuẩn hóa để phân biệt retry cùng yêu cầu với tái sử dụng key cho payload khác.
    public static string ComputeFingerprint<T>(T value)
    {
        // Đóng gói snapshot/payload đang xử lý để lưu hoặc truyền theo contract; giữ dữ liệu của lần quyết định hiện tại.
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[] hash = SHA256.HashData(jsonBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Băm nội dung đã chuẩn hóa để phân biệt retry cùng yêu cầu với tái sử dụng key cho payload khác.
    public static string ComputeFingerprint(string input)
    {
        // Tạo fingerprint nội dung để nhận diện payload đã lưu; không dùng fingerprint thay cho kiểm tra quyền sở hữu.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    // Điểm vào recovery worker: descriptor chỉ là ứng viên; phải đọc lại receipt dưới order lock.
    // Bỏ qua tác vụ đã thành công/chưa đến lịch, đợi prerequisite rồi mới thực thi đúng nhánh Action.
    public async Task<Result> ExecuteScheduledOperationAsync(
        OperationReceiptDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        // Kiểm tra lịch và ngân sách retry để worker không thực thi quá sớm hoặc lặp vô hạn.
        if (descriptor.RetryCount >= 10 && descriptor.Action != "CreateInvoice")
        {
            return Result.Success();
        }

        // Kiểm tra lịch và ngân sách retry để worker không thực thi quá sớm hoặc lặp vô hạn.
        if (descriptor.NextRetryAt.HasValue && descriptor.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(descriptor.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken);
        utcNow = _timeProvider.GetUtcNow();

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(descriptor.RentalOrderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        // Descriptor từ lần quét có thể đã cũ. Receipt vừa đọc dưới khóa mới quyết định
        // có chạy hay không, tránh worker thực thi lại thao tác API/worker khác đã hoàn tất.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == descriptor.OperationId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();


        // Phân biệt receipt đã kết thúc lỗi với trường hợp được phép mở lại theo quy tắc recovery.
        if (receipt.Status == RentalOperationReceipt.StatusFailed)
        {
            // Dừng nhánh không hợp lệ với mã INVOICE_ISSUER_UNAVAILABLE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (receipt.Action == "CreateInvoice" &&
                receipt.ErrorClassification is RentalOperationReceipt.ClassificationTransientExhausted or RentalOperationReceipt.ClassificationUncertainExhausted)
            {
                // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
                if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
                {
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (_invoiceIssuer == null)
                    {
                        return Error.Failure("INVOICE_ISSUER_UNAVAILABLE", "Invoice issuer is not configured.");
                    }
                    return await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
                }

                // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
                if (utcNow >= order.PaymentDeadline || order.Status != RentalOrderStatus.PendingPayment)
                {
                    // Phân loại dừng vì hết hạn hoặc order không còn chờ thanh toán để journal không bị retry mãi cùng yêu cầu không hợp lệ.
                    string closeCode = utcNow >= order.PaymentDeadline ? "ORDER_EXPIRED" : "ORDER_NOT_PENDING";
                    string closeMsg = utcNow >= order.PaymentDeadline
                        ? "Payment deadline has passed; halting invoice creation."
                        : "Order is no longer pending payment; halting invoice creation.";
                    receipt.MarkFailed(closeCode, closeMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (_checkoutOperationRepo != null)
                    {
                        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
                        var op = await _checkoutOperationRepo.GetByExpectedOrderIdAsync(order.Id, cancellationToken);
                        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                        if (op != null && op.InvoiceStatus != "Failed")
                        {
                            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                            op.SetInvoiceStatus("Failed", utcNow);
                            await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
                        }
                    }
                    await CommitLocalChangesAsync(order, cancellationToken);
                    return Result.Success();
                }

                // Mở lại receipt theo quy tắc recovery/replay hiện có; giữ journal và dấu kết quả bất định thay vì tạo tác vụ thay thế.
                receipt.ResumeRetry(utcNow);
                await CommitLocalChangesAsync(order, cancellationToken);
            }
            else
            {
                return Result.Success();
            }
        }

        // Kiểm tra lịch và ngân sách retry để worker không thực thi quá sớm hoặc lặp vô hạn.
        if (receipt.NextRetryAt.HasValue && receipt.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        // Chỉ thực hiện tác vụ phụ thuộc khi receipt tiên quyết đã được xác nhận Succeeded.
        if (!string.IsNullOrWhiteSpace(receipt.PrerequisiteOperationId))
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var prereq = order.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.PrerequisiteOperationId);
            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (prereq == null || !prereq.IsSuccess || prereq.Status != RentalOperationReceipt.StatusSucceeded)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogInformation("Scheduled operation {OpId} waiting for prerequisite {Prereq}", receipt.OperationId, receipt.PrerequisiteOperationId);
                return Result.Success();
            }
        }

        // Dispatch theo action đã lưu trong receipt; mỗi nhánh dùng use case tương ứng, action không hỗ trợ phải trả lỗi.
        switch (receipt.Action)
        {
            case RentalItem.OverdueProtectionAction:
                {
                    // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (item is null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await MarkAssetsOverdueCoreAsync(order, item, receipt, cancellationToken);
                }
            case "ExtendHold":
                {
                    // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await ExtendHoldCoreAsync(order, item, receipt, cancellationToken);
                }

            case "SupplierAccept":
                {
                    // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                    if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
                    {
                        return Error.Conflict("ITEM_IN_TERMINAL_STATE", $"Mục thuê đã ở trạng thái {item.Status}; không thể áp dụng accept.");
                    }
                    var acceptResult = await ConfirmAllocationCoreAsync(order, item, receipt, receipt.OperationId, cancellationToken);
                    return acceptResult.IsSuccess ? Result.Success() : acceptResult.Error;
                }

            case "ReleaseAllocation":
                {
                    // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await ReleaseAllocationCoreAsync(order, item, receipt, receipt.OperationId, cancellationToken);
                }

            case "CancelInvoice":
                return await CancelInvoiceCoreAsync(order, receipt, receipt.OperationId, cancellationToken);

            case "RequestRefund":
                {
                    // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await RequestRefundCoreAsync(order, item, receipt, receipt.OperationId, cancellationToken);
                }

            case "PaymentExpiry":
                return await PaymentExpiryCoreAsync(order, receipt, cancellationToken);

            case RentalItem.SettleRentalAction:
                {
                    // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (item is null) return Error.NotFound("ITEM_NOT_FOUND", "Rental item was not found.");
                    return await SettleRentalCoreAsync(order, item, receipt, cancellationToken);
                }

            case "CreateInvoice":
                return await ExecuteCreateInvoiceAsync(order, receipt, cancellationToken);
            default:
                return Error.Validation("UNSUPPORTED_ACTION", $"Hành động {receipt.Action} không hỗ trợ scheduled execution.");
        }
    }

    // Kiểm tra yêu cầu replay, journal và lịch sử idempotency; ghi audit và mở lại receipt để worker thực thi tiếp.
    public async Task<Result<AdminReplayResponse>> ExecuteAdminReplayAsync(
        Guid orderId,
        string operationId,
        Guid adminActorId,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Dừng nhánh không hợp lệ với mã AUTH_REQUIRED; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (adminActorId == Guid.Empty)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu danh tính Admin hợp lệ.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string trimmedReason = reason?.Trim() ?? string.Empty;
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(trimmedReason) || trimmedReason.Length > 1000)
        {
            return Error.Validation("INVALID_REASON", "Lý do replay là bắt buộc và tối đa 1000 ký tự.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string normalizedIdempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(normalizedIdempotencyKey) || normalizedIdempotencyKey.Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và tối đa 64 ký tự.");
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{orderId}'.");

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (receipt == null) return Error.NotFound("OPERATION_NOT_FOUND", $"Không tìm thấy operation '{operationId}'.");

        // Tác vụ cần xử lý thủ công không được tự động replay vì kết quả cũ chưa đủ chắc chắn.
        if (receipt.Status == RentalOperationReceipt.StatusManualRefundRequired)
        {
            return Error.Conflict("MANUAL_REFUND_REQUIRED", "Refund receipt is reserved for manual reconciliation and cannot be replayed automatically.");
        }

        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return Error.Conflict("OPERATION_ALREADY_SUCCEEDED", "Operation đã hoàn thành thành công (Succeeded); không thể replay.");
        }

        // Validate journal command payload
        if (string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            return Error.Unprocessable("CORRUPTED_JOURNAL", "Journal payload bị thiếu; không thể replay an toàn.");
        }

        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
            using var doc = JsonDocument.Parse(receipt.PayloadJson);
        }
        // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
        catch
        {
            return Error.Unprocessable("CORRUPTED_JOURNAL", "Journal payload bị hỏng; không thể replay an toàn.");
        }

        // Idempotency check for replay request
        var existingReplayAudit = order.StateTransitions.FirstOrDefault(t => t.OperationId == normalizedIdempotencyKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReplayAudit != null)
        {
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (string.Equals(existingReplayAudit.ToState, RentalOperationReceipt.StatusPending, StringComparison.OrdinalIgnoreCase) &&
                existingReplayAudit.Reason != null &&
                existingReplayAudit.Reason.Contains(operationId, StringComparison.OrdinalIgnoreCase) &&
                existingReplayAudit.Reason.Contains(trimmedReason, StringComparison.OrdinalIgnoreCase))
            {
                return new AdminReplayResponse(order.Id, receipt.OperationId, receipt.Action, receipt.Status, receipt.RetryCount, existingReplayAudit.OccurredAt);
            }

            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa Idempotency-Key đã được sử dụng với nội dung khác.");
        }

        // Audit transition
        order.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            order.Id,
            receipt.TargetItemId,
            receipt.Status,
            RentalOperationReceipt.StatusPending,
            "AdminReplayRequested",
            null,
            ActorRole.Admin,
            $"Admin {adminActorId} requested replay for {operationId} (Previous: {receipt.Status}/{receipt.ErrorClassification}, Retries: {receipt.RetryCount}). Reason: {trimmedReason}",
            utcNow,
            normalizedIdempotencyKey));

        // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
        order.RecordReadModelUpdate(utcNow);

        // Reset retry budget to 0, status to Pending, NextRetryAt to now, KEEP IsOutcomeUncertain!
        receipt.ReopenForManualReplay(utcNow);

        // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
        await CommitLocalChangesAsync(order, cancellationToken);

        // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
        _logger.LogInformation("Admin {AdminId} successfully scheduled replay for Operation {OpId} (Order: {OrderId})",
            adminActorId, operationId, order.Id);

        return new AdminReplayResponse(order.Id, receipt.OperationId, receipt.Action, receipt.Status, receipt.RetryCount, utcNow);
    }

    // Mở lại receipt dưới khóa, lưu descriptor rồi nhả khóa trước khi vào executor vốn tự lấy khóa order.
    public async Task<Result> ReplayStoredOperationAsync(
        Guid orderId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        OperationReceiptDescriptor? desc = null;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

            // Tác vụ cần xử lý thủ công không được tự động replay vì kết quả cũ chưa đủ chắc chắn.
            if (receipt.Status == RentalOperationReceipt.StatusManualRefundRequired)
            {
                return Error.Conflict("MANUAL_REFUND_REQUIRED", "Refund receipt is reserved for manual reconciliation and cannot be replayed automatically.");
            }

            // Mở lại receipt theo quy tắc recovery/replay hiện có; giữ journal và dấu kết quả bất định thay vì tạo tác vụ thay thế.
            receipt.ReopenForManualReplay(utcNow);
            await CommitLocalChangesAsync(order, cancellationToken);

            // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
            desc = new OperationReceiptDescriptor(
                receipt.Id,
                order.Id,
                receipt.OperationId,
                receipt.Action,
                receipt.TargetItemId,
                receipt.Status,
                receipt.RetryCount,
                receipt.NextRetryAt,
                receipt.CreatedAt,
                receipt.PrerequisiteOperationId,
                receipt.IsOutcomeUncertain);
        }

        return await ExecuteScheduledOperationAsync(desc!, cancellationToken);
    }
    // Phân biệt lỗi nghiệp vụ, lỗi tạm thời và kết quả chưa rõ; lập lịch backoff hoặc kết thúc ngân sách retry theo action.
    private static void RecordExecutionFailure(
        RentalOperationReceipt receipt,
        Error error,
        DateTimeOffset utcNow,
        bool forceUncertain = false,
        DateTimeOffset? deadline = null)
    {
        // Tạo invoice có lịch retry theo deadline riêng; các action còn lại dùng ngân sách retry mặc định.
        bool isCreateInvoice = receipt.Action == "CreateInvoice";

        // Dừng ngay lỗi invoice đã xác định chắc chắn, không để cờ uncertainty cũ biến từ chối nghiệp vụ thành retry vô hạn.
        if (isCreateInvoice && IsDeterministicInvoiceRejection(error))
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            receipt.MarkFailed(error.Code, error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            return;
        }
        // Timeout có thể xảy ra sau khi provider đã commit. Giữ cờ outcome-uncertain
        // qua các lần thử để không nhầm mất phản hồi thành thất bại chắc chắn và bù trừ sai.
        bool isUncertain = forceUncertain ||
                           receipt.IsOutcomeUncertain ||
                           error.Type == ErrorType.ServiceUnavailable ||
                           error.Code.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                           error.Code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);

        // Chỉ lập NextRetryAt khi còn ngân sách của action; hết ngân sách giữ trạng thái để đối soát hoặc xử lý thủ công.
        bool isExhausted = !isCreateInvoice && receipt.RetryCount >= 10;

        // Backoff tăng dần nhưng tối đa 60 giây; jitter tránh nhiều receipt retry cùng lúc.
        // CreateInvoice có quy tắc riêng: lịch retry không vượt deadline được truyền vào.
        double baseDelay = Math.Min(60, Math.Pow(2, receipt.RetryCount));
        int jitterMs = Random.Shared.Next(0, 1000);
        DateTimeOffset? nextRetry = null;
        // Chỉ lập NextRetryAt khi còn ngân sách của action; hết ngân sách giữ trạng thái để đối soát hoặc xử lý thủ công.
        if (!isExhausted)
        {
            // Tính thời điểm retry bằng backoff cộng jitter để phân tán các lần gọi lại giữa nhiều receipt.
            var scheduled = utcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (isCreateInvoice && deadline.HasValue && scheduled > deadline.Value)
            {
                // Không lên lịch retry invoice sau payment deadline; mốc cuối được chặn bởi deadline gốc của order.
                scheduled = deadline.Value;
            }
            nextRetry = scheduled;
        }

        // Bảo toàn khả năng provider đã commit khi mất ACK; receipt phải giữ phân loại uncertain cho lần đối soát tiếp theo.
        if (isUncertain)
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            receipt.RecordAttemptFailure(error.Code, error.Message, utcNow, nextRetry, isUncertain: true, isExhausted: isExhausted);
            return;
        }

        // Lỗi Failure còn được xử lý theo lịch retry tạm thời; lỗi nghiệp vụ chắc chắn nằm ở nhánh kết thúc phía sau.
        if (error.Type == ErrorType.Failure)
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            receipt.RecordAttemptFailure(error.Code, error.Message, utcNow, nextRetry, isUncertain: false, isExhausted: isExhausted);
            return;
        }

        // Khi chưa từng có kết quả bất định, lỗi nghiệp vụ chắc chắn không được retry.
        // Retry cùng dữ liệu sai chỉ tăng tải; nhánh isUncertain phía trên phải được xét trước.
        receipt.MarkFailed(error.Code, error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
    }
    // Nhận diện các lỗi tạo hóa đơn chắc chắn không thể sửa bằng retry cùng request.
    private static bool IsDeterministicInvoiceRejection(Error error) =>
        string.Equals(error.Code, CheckoutErrors.InvoiceRejected.Code, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(error.Code, CheckoutErrors.OrderExpired.Code, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(error.Code, "ORDER_NOT_PENDING", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(error.Code, "ORDER_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
        error.Type is ErrorType.Validation or ErrorType.Conflict;


    // Đăng ký graph order với repository và lưu UnitOfWork; ranh giới transaction do luồng gọi quyết định.
    private async Task CommitLocalChangesAsync(RentalOrder order, CancellationToken cancellationToken)
    {
        // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
