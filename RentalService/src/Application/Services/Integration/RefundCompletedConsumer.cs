#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.IServices.Integration;
using Microsoft.Extensions.Logging;
using Dprental.RentalService.Application.Policies;

namespace Dprental.RentalService.Application.Services.Integration;

/// <remarks>
/// Áp dụng kết quả hoàn tiền do Payment phát: kiểm tra định danh và dữ liệu liên quan trước khi cập nhật Rental.
/// Nhánh Staged tham gia transaction của dispatcher; không nhầm với thao tác gửi yêu cầu RequestRefund trước đó.
/// </remarks>
public sealed class RefundCompletedConsumer : IRefundCompletedConsumer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefundCompletedConsumer> _logger;

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public RefundCompletedConsumer(
        IRentalOrderRepository orderRepository,
        IInboxRepository inboxRepository,
        IOrderLockManager lockManager,
        IUnitOfWork unitOfWork,
        ILogger<RefundCompletedConsumer> logger,
        TimeProvider? timeProvider = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Đối soát event với request refund đã lưu, từng khoản tiền và kết quả cũ; stage thay đổi để caller commit cùng Inbox.
    public async Task<Result<bool>> ApplyRefundCompletedStagedAsync(
        RentalOrder order,
        KafkaMessageEnvelope<RefundCompletedPayload> envelope,
        RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default)
    {
        // Lấy payload của envelope đã đi qua bước kiểm tra đầu vào để xử lý cùng một bản tin xuyên suốt use case.
        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // 1. Validate references & currency
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef) &&
            !string.Equals(order.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("INVOICE_REF_MISMATCH", "InvoiceRef trong sự kiện hoàn tiền không khớp với đơn hàng.");
        }

        // Đối chiếu tham chiếu thanh toán để tách khoản tiền đã ghi nhận khỏi một thanh toán khác hoặc sự kiện lặp.
        if (!string.IsNullOrWhiteSpace(order.PaymentRef) &&
            !string.Equals(order.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("PAYMENT_REF_MISMATCH", "PaymentRef trong sự kiện hoàn tiền không khớp với đơn hàng.");
        }

        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (!string.Equals(payload.Currency, "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("CURRENCY_MISMATCH", "Tiền tệ phải là VND.");
        }

        // 2. Validate operation receipt matching
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == payload.OperationKey);
        // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
        if (receipt == null || receipt.Action != "RequestRefund")
        {
            return Error.Validation("INVALID_OPERATION_KEY", "Không tìm thấy yêu cầu hoàn tiền tương ứng trong đơn hàng.");
        }

        // 3. Stored refund request must exist, parse, and validate
        if (string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            return Error.Validation("MISSING_STORED_REQUEST", "Thiếu request payload JSON cho RequestRefund.");
        }

        // Chuẩn bị đọc request refund đã lưu; journal là căn cứ so từng khoản hoàn, không dùng số tiền hiện tại suy đoán.
        RequestRefundRequest? storedRequest = null;
        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
            using var doc = JsonDocument.Parse(receipt.PayloadJson);
            // Đọc trường có trong payload journal; hỗ trợ đúng dạng dữ liệu đã lưu thay vì giả định mọi receipt cùng cấu trúc.
            if (doc.RootElement.TryGetProperty("providerRequestJson", out var providerJsonProp))
            {
                // Chuẩn bị đọc request refund đã lưu; journal là căn cứ so từng khoản hoàn, không dùng số tiền hiện tại suy đoán.
                string? providerJson = providerJsonProp.GetString();
                // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
                if (!string.IsNullOrWhiteSpace(providerJson))
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    storedRequest = JsonSerializer.Deserialize<RequestRefundRequest>(providerJson, JsonOptions);
                }
            }
            else
            {
                // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                storedRequest = JsonSerializer.Deserialize<RequestRefundRequest>(receipt.PayloadJson, JsonOptions);
            }
        }
        // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
        catch (Exception ex)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogWarning(ex, "Failed to parse stored RequestRefund payload for {OpKey}", receipt.OperationId);
            return Error.Validation("CORRUPTED_STORED_REQUEST", "Stored RequestRefund payload bị hỏng; không thể đối soát.");
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (storedRequest == null)
        {
            return Error.Validation("CORRUPTED_STORED_REQUEST", "Không thể trích xuất RequestRefund request từ journal.");
        }

        // Validate item membership, uniqueness, breakdown, and totals
        var payloadItemIds = payload.Items.Select(i => i.RentalItemId).ToList();
        // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
        if (payloadItemIds.Distinct().Count() != payloadItemIds.Count)
        {
            return Error.Validation("DUPLICATE_ITEM_REFUND", "Sự kiện chứa RentalItemId trùng lặp.");
        }

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (payload.Items.Count != storedRequest.Items.Count)
        {
            return Error.Validation("ITEM_COUNT_MISMATCH", "Số lượng mục hoàn tiền trong sự kiện không khớp với yêu cầu.");
        }

        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var pItem in payload.Items)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var storedItem = storedRequest.Items.FirstOrDefault(i => i.RentalItemId == pItem.RentalItemId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (storedItem == null)
            {
                return Error.Validation("ITEM_MEMBERSHIP_MISMATCH", $"Mục thuê {pItem.RentalItemId} không thuộc yêu cầu hoàn tiền.");
            }

            // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
            if (storedItem.RefundRentalAmount != pItem.RefundedRentalAmount ||
                storedItem.RefundDepositAmount != pItem.RefundedDepositAmount)
            {
                return Error.Validation("AMOUNT_BREAKDOWN_MISMATCH", $"Số tiền hoàn của mục {pItem.RentalItemId} không khớp với yêu cầu.");
            }
        }

        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (storedRequest.TotalRefundAmount != payload.TotalRefundedAmount)
        {
            return Error.Validation("TOTAL_REFUND_MISMATCH", "Tổng số tiền hoàn không khớp với yêu cầu.");
        }

        // 4. Validate against stored ACK if present
        if (!string.IsNullOrWhiteSpace(receipt.ResponseJson))
        {
            // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
            try
            {
                // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                var ack = JsonSerializer.Deserialize<RequestRefundResponse>(receipt.ResponseJson, JsonOptions);
                // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
                if (ack != null && !string.IsNullOrWhiteSpace(ack.RefundRef) &&
                    !string.Equals(ack.RefundRef, payload.RefundRef, StringComparison.OrdinalIgnoreCase))
                {
                    return Error.Conflict("REFUND_REF_MISMATCH", $"RefundRef ({payload.RefundRef}) không khớp với ACK đã lưu ({ack.RefundRef}).");
                }
            }
            // Giữ nhánh bỏ qua lỗi đọc dữ liệu cũ: phần sau phải kiểm tra lại kết quả và chọn fallback/lỗi, không coi parse lỗi là dữ liệu hợp lệ.
            catch
            {
                // Ignore parse errors on responseJson if non-fatal
            }
        }

        // 5. Check if already completed (RefundCompletionJson already recorded)
        if (!string.IsNullOrWhiteSpace(receipt.RefundCompletionJson))
        {
            // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
            try
            {
                // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                var existingCompletion = JsonSerializer.Deserialize<RefundCompletedPayload>(receipt.RefundCompletionJson, JsonOptions);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (existingCompletion != null &&
                    string.Equals(existingCompletion.RefundRef, payload.RefundRef, StringComparison.OrdinalIgnoreCase) &&
                    existingCompletion.TotalRefundedAmount == payload.TotalRefundedAmount)
                {
                    // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                    _logger.LogInformation("RefundCompleted event {MessageId} replayed for already completed refund {RefundRef}",
                        envelope.MessageId, payload.RefundRef);
                    return Result.Success(false);
                }
            }
            // Giữ nhánh bỏ qua lỗi đọc dữ liệu cũ: phần sau phải kiểm tra lại kết quả và chọn fallback/lỗi, không coi parse lỗi là dữ liệu hợp lệ.
            catch
            {
                // Fall through to conflict
            }
            return Error.Conflict("REFUND_COMPLETION_CONFLICT", "Sự kiện hoàn tiền mâu thuẫn với kết quả hoàn tiền đã được ghi nhận trước đó.");
        }

        // 6. Mutate data: Record refund reference and financial audit once
        foreach (var refundedItem in payload.Items)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var item = order.Items.FirstOrDefault(i => i.Id == refundedItem.RentalItemId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (item != null)
            {
                // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                item.RecordRefund(payload.RefundRef);

                // Lưu dấu chuyển trạng thái kèm actor và operation để đối soát lịch sử của quyết định này.
                order.AddStateTransition(new RentalStateTransition(
                    Guid.NewGuid(),
                    order.Id,
                    item.Id,
                    item.Status.ToString(),
                    item.Status.ToString(),
                    "RefundCompleted",
                    null,
                    ActorRole.System,
                    $"Refund confirmed: fee {refundedItem.RefundedRentalAmount:N0} VND, deposit {refundedItem.RefundedDepositAmount:N0} VND (RefundRef: {payload.RefundRef})",
                    utcNow,
                    payload.OperationKey));
            }
        }

        // Save immutable RefundCompletionJson separately from ACK ResponseJson
        receipt.SetRefundCompletion(JsonSerializer.Serialize(payload, JsonOptions), utcNow);
        receipt.MarkSucceeded(utcNow);

        // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
        RentalOrderStatusPolicy.Apply(order, utcNow, payload.OperationKey);
        order.RecordReadModelUpdate(utcNow);

        return Result.Success(true);
    }

    // Validate envelope, khóa order và deduplicate MessageId; ghi kết quả hoàn tiền cùng Inbox Processed trong một transaction.
    public async Task<Result> ProcessRefundCompletedAsync(
        KafkaMessageEnvelope<RefundCompletedPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (envelope == null || envelope.Payload == null)
        {
            return Error.Validation("INVALID_ENVELOPE", "Envelope không được rỗng.");
        }

        // Dừng nhánh không hợp lệ với mã UNSUPPORTED_VERSION; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (envelope.Version != 1)
        {
            return Error.Validation("UNSUPPORTED_VERSION", "Chỉ hỗ trợ envelope version 1.");
        }

        // Dừng nhánh không hợp lệ với mã INVALID_MESSAGE_TYPE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!string.Equals(envelope.MessageType, "RefundCompleted", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("INVALID_MESSAGE_TYPE", "MessageType phải là RefundCompleted.");
        }

        // Dừng nhánh không hợp lệ với mã INVALID_PRODUCER; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!string.Equals(envelope.Producer, "payment-service", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("INVALID_PRODUCER", "Producer phải là payment-service.");
        }

        // Lấy payload của envelope đã đi qua bước kiểm tra đầu vào để xử lý cùng một bản tin xuyên suốt use case.
        var payload = envelope.Payload;
        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!Guid.TryParse(envelope.AggregateId, out var aggId) || aggId != payload.RentalOrderId)
        {
            return Error.Validation("AGGREGATE_ID_MISMATCH", "AggregateId không khớp với RentalOrderId.");
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(payload.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đóng gói snapshot/payload đang xử lý để lưu hoặc truyền theo contract; giữ dữ liệu của lần quyết định hiện tại.
        string rawPayload = JsonSerializer.Serialize(envelope, JsonOptions);
        string payloadFingerprint = ComputeHash(rawPayload);

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var existingInbox = await _inboxRepository.GetByMessageIdAsync(envelope.MessageId, cancellationToken);
        // Inbox đã Processed thì bản tin là replay theo MessageId; không áp dụng nghiệp vụ lần thứ hai.
        if (existingInbox != null && existingInbox.Status == RentalInboxMessage.StatusProcessed)
        {
            return Result.Success();
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(payload.RentalOrderId, cancellationToken);
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
            payloadFingerprint);

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (existingInbox == null)
            {
                // Đăng ký Inbox mới trong cùng transaction áp dụng bản tin để chống xử lý lặp sau khi consumer khởi động lại.
                await _inboxRepository.AddAsync(inboxMessage, cancellationToken);
            }

            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var result = await ApplyRefundCompletedStagedAsync(order, envelope, inboxMessage, cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (result.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return result;
            }

            // Đánh dấu Inbox đã áp dụng; trạng thái này phải được lưu cùng các thay đổi nghiệp vụ của bản tin.
            inboxMessage.MarkProcessed(utcNow);
            _inboxRepository.Update(inboxMessage);

            // Chỉ đăng ký cập nhật order khi bước áp dụng báo có thay đổi; Inbox vẫn cần được đánh dấu đã xử lý.
            if (result.Value)
            {
                // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
                await _orderRepository.UpdateAsync(order, cancellationToken);
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success();
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch (Exception ex)
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger.LogError(ex, "Transaction failed while processing RefundCompleted message {MessageId}", envelope.MessageId);
            throw;
        }
    }

    // Tạo fingerprint của envelope đã serialize để lưu dấu nội dung bản tin nhận được trong Inbox.
    private static string ComputeHash(string input)
    {
        // Tạo fingerprint nội dung để nhận diện payload đã lưu; không dùng fingerprint thay cho kiểm tra quyền sở hữu.
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
