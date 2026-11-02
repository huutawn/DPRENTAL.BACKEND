using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Errors;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Các nhánh tác vụ Payment của cùng executor: hủy invoice, yêu cầu hoàn tiền, hết hạn thanh toán và tạo invoice.
/// Dùng receipt/operation key để theo dõi lần gọi HTTP và phục hồi khi gián đoạn.
/// Payment chấp nhận yêu cầu hoàn tiền chưa phải bằng chứng tiền đã hoàn; sự kiện RefundCompleted cập nhật kết quả sau đó.
/// </remarks>
public sealed partial class RentalOperationExecutor
{
    // Lấy khóa order, đọc receipt rồi điều phối hủy invoice bằng key của operation đã lưu.
    public async Task<Result> ExecuteCancelInvoiceAsync(
        Guid orderId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (string.IsNullOrWhiteSpace(order.InvoiceRef)) return Result.Success();

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

        return await CancelInvoiceInternalAsync(order, receipt, operationKey, cancellationToken);
    }

    // Chọn định danh provider của receipt và chuyển về nhánh hủy hóa đơn thống nhất.
    internal async Task<Result> CancelInvoiceCoreAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        return await CancelInvoiceInternalAsync(order, receipt, operationKey, cancellationToken);
    }

    // Ghi attempt trước HTTP Payment, dùng response để chốt receipt hoặc lưu lỗi retry; không coi invoice đã trả tiền là hủy được.
    internal async Task<Result> CancelInvoiceInternalAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (string.IsNullOrWhiteSpace(order.InvoiceRef)) return Result.Success();
        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Khai báo request cần khôi phục từ journal trước khi thực thi; thiếu/hỏng payload phải dừng để đối soát.
        CancelInvoiceRequest request;
        // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
            try
            {
                // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                using var doc = JsonDocument.Parse(receipt.PayloadJson);
                // Đọc trường có trong payload journal; hỗ trợ đúng dạng dữ liệu đã lưu thay vì giả định mọi receipt cùng cấu trúc.
                if (doc.RootElement.TryGetProperty("providerRequestJson", out var provProp))
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    request = JsonSerializer.Deserialize<CancelInvoiceRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    request = JsonSerializer.Deserialize<CancelInvoiceRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của CancelInvoice bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho CancelInvoice.");
        }
        var result = await _paymentClient.CancelInvoiceAsync(request, cancellationToken);

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        utcNow = _timeProvider.GetUtcNow();
        // Dừng nhánh không hợp lệ với mã CANCEL_UNCONFIRMED; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (result.IsSuccess)
        {
            // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
            if (!result.Value.Cancelled || !string.Equals(result.Value.InvoiceRef, order.InvoiceRef, StringComparison.OrdinalIgnoreCase))
            {
                // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                RecordExecutionFailure(receipt, Error.Validation("CANCEL_UNCONFIRMED", "PaymentService không xác nhận hủy invoice (Cancelled != true hoặc InvoiceRef sai)."), utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return Error.Unprocessable("CANCEL_UNCONFIRMED", "PaymentService không xác nhận hủy invoice.");
            }
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(result.Value, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        // Điều kiện này quyết định có thể tiếp tục hay phải trả Error.Conflict theo contract của use case.
        if (string.Equals(result.Error.Code, PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, StringComparison.OrdinalIgnoreCase))
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            receipt.MarkFailed(result.Error.Code, result.Error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Error.Conflict(PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, "Invoice was already paid; halting expiry.");
        }

        // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return result.Error;
    }

    // Khóa order, tìm item và journal refund trước khi gọi nhánh hoàn tiền chung.
    public async Task<Result> ExecuteRequestRefundAsync(
        Guid orderId,
        Guid itemId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy receipt.");

        return await RequestRefundInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    // Dùng nhánh refund chung với order/item/receipt caller đã chuẩn bị; không sinh yêu cầu chuyển tiền song song.
    public Task<Result> RequestRefundDirectAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        return RequestRefundInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    // Thực thi refund từ receipt đã lưu để worker và API dùng cùng payload/idempotency.
    internal async Task<Result> RequestRefundCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        return await RequestRefundInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    // Đọc request refund từ journal, xử lý ACK và lỗi; ACK nhận yêu cầu chưa phải bằng chứng tiền đã được hoàn.
    internal async Task<Result> RequestRefundInternalAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        // Tác vụ cần xử lý thủ công không được tự động replay vì kết quả cũ chưa đủ chắc chắn.
        if (receipt.Status == RentalOperationReceipt.StatusManualRefundRequired)
        {
            return Error.Conflict("MANUAL_REFUND_REQUIRED", "Refund receipt is reserved for manual reconciliation and cannot be replayed automatically.");
        }

        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();
        // Strict prerequisite check: ReleaseAllocation must be confirmed Succeeded
        if (!string.IsNullOrWhiteSpace(receipt.PrerequisiteOperationId))
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var prereq = order.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.PrerequisiteOperationId);
            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (prereq == null || !prereq.IsSuccess || prereq.Status != RentalOperationReceipt.StatusSucceeded)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogInformation("Refund operation {OpKey} waiting for prerequisite {Prereq}", operationKey, receipt.PrerequisiteOperationId);
                return Result.Success();
            }
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        RequestRefundRequest request;
        // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
            try
            {
                // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                using var doc = JsonDocument.Parse(receipt.PayloadJson);
                // Đọc trường có trong payload journal; hỗ trợ đúng dạng dữ liệu đã lưu thay vì giả định mọi receipt cùng cấu trúc.
                if (doc.RootElement.TryGetProperty("providerRequestJson", out var provProp))
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    request = JsonSerializer.Deserialize<RequestRefundRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    request = JsonSerializer.Deserialize<RequestRefundRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của RequestRefund bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho RequestRefund.");
        }
        var requestValidation = request.Validate();
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (requestValidation.IsFailure)
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            RecordExecutionFailure(receipt, requestValidation.Error, utcNow);
            await CommitLocalChangesAsync(order, cancellationToken);
            return requestValidation.Error;
        }

        // Amount = 0 does not disburse
        if (request.TotalRefundAmount <= 0)
        {
            // Ghi kết quả thành công của receipt; ý nghĩa hoàn tất nghiệp vụ còn phụ thuộc action và event xác nhận riêng.
            receipt.MarkSucceeded(utcNow, "{\"zeroAmount\":true}");
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        // Gọi service sở hữu dữ liệu/tác vụ bằng contract đã chuẩn bị; kết quả phải được kiểm tra trước khi chốt trạng thái local.
        var result = await _paymentClient.RequestRefundAsync(request, cancellationToken);

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        utcNow = _timeProvider.GetUtcNow();
        // Dừng nhánh không hợp lệ với mã INVALID_REFUND_RESPONSE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (result.IsSuccess)
        {
            // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
            var refundData = result.Value;
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (string.IsNullOrWhiteSpace(refundData.RefundRef) ||
                !string.Equals(refundData.Status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(refundData.OperationKey, operationKey, StringComparison.OrdinalIgnoreCase) ||
                refundData.TotalRefundAmount != request.TotalRefundAmount ||
                !string.Equals(refundData.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            {
                // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                RecordExecutionFailure(receipt, Error.Validation("INVALID_REFUND_RESPONSE", "Phản hồi từ PaymentService không hợp lệ."), utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return Error.Unprocessable("INVALID_REFUND_RESPONSE", "Phản hồi hoàn tiền từ PaymentService không hợp lệ.");
            }
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(refundData, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return result.Error;
    }

    // Khóa và kiểm tra lại order trước khi hết hạn; điều phối hủy invoice, release hold và event theo trạng thái thanh toán hiện có.
    public async Task<Result> ExecutePaymentExpiryAsync(
        Guid orderId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (order == null || order.Status != RentalOrderStatus.PendingPayment) return Result.Success();

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (utcNow < order.PaymentDeadline) return Result.Success();

        // 1. Cancel invoice first. An order whose invoice was never created (Payment was down for the whole
        //    payment window) has nothing to cancel, and a receipt for it would stay Pending forever.
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
                    payloadJson: JsonSerializer.Serialize(new CancelInvoiceRequest(order.Id, order.InvoiceRef, "Payment window expired", cancelOpKey), JsonOptions));
                order.AddOperationReceipt(cancelReceipt);
                await CommitLocalChangesAsync(order, cancellationToken);
            }

            // Xử lý hủy invoice trước khi chốt hủy/quá hạn; Payment báo đã thu tiền thì không tiếp tục như đơn chưa trả tiền.
            var cancelRes = await CancelInvoiceInternalAsync(order, cancelReceipt, cancelOpKey, cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (cancelRes.IsFailure)
            {
                // Điều kiện này quyết định có thể tiếp tục hay phải trả Result.Success theo contract của use case.
                if (string.Equals(cancelRes.Error.Code, PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, StringComparison.OrdinalIgnoreCase))
                {
                    // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                    _logger.LogInformation("Invoice was already collected for Order {OrderId}. Halting expiry and waiting for InvoicePaid event.", order.Id);
                    return Result.Success(); // Halts expiry! Does NOT release holds or expire order.
                }
                return cancelRes;
            }
        }

        // 2. Release all allocation groups
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
                    payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseKey, "Payment window expired"), JsonOptions));
                order.AddOperationReceipt(releaseReceipt);
            }
            releaseKeys.Add((item, releaseKey));
        }

        // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
        await CommitLocalChangesAsync(order, cancellationToken);

        // Theo dõi kết quả lập lệnh release cho mọi dòng; không nhầm thành công enqueue với xác nhận allocation đã đóng.
        bool allReleased = true;
        foreach (var (item, releaseKey) in releaseKeys)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var receipt = order.OperationReceipts.First(r => r.OperationId == releaseKey);
            var relRes = await ReleaseAllocationInternalAsync(order, item, receipt, releaseKey, cancellationToken);
            // Trả lỗi từ bước kiểm tra vừa chạy; không thực hiện phần ghi hoặc gọi provider tiếp theo khi điều kiện chưa đạt.
            if (relRes.IsFailure) allReleased = false;
        }

        // Dừng nhánh không hợp lệ với mã RELEASE_PENDING; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!allReleased)
        {
            return Error.ServiceUnavailable("RELEASE_PENDING", "Một số allocations chưa hoàn tất release; sẽ retry qua worker.");
        }

        // 3. Mark Expired ONLY after valid cancel and all releases succeeded
        utcNow = _timeProvider.GetUtcNow();
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Áp dụng quyết định trạng thái đã qua các điều kiện của luồng; sau đó lưu history/receipt và side effect liên quan.
            order.MarkPaymentExpired(utcNow, operationKey);
            order.RecordReadModelUpdate(utcNow);

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var expiredRes = await _rentalEvents.EnqueueRentalOrderExpiredAsync(
                new RentalOrderExpiredPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.PaymentExpired, order.Version),
                correlationId: operationKey,
                cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (expiredRes.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return expiredRes.Error;
            }
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
                        OrderChangeTypes.PaymentExpired,
                        utcNow),
                    cancellationToken);
            }
            return Result.Success();
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

    // Chuyển receipt PaymentExpiry về use case hết hạn chung, giữ một đường thực thi cho API/worker.
    internal async Task<Result> PaymentExpiryCoreAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        return await ExecutePaymentExpiryAsync(order.Id, receipt.OperationId, cancellationToken);
    }

    // Recovery hóa đơn từ receipt: kiểm tra order/deadline, gọi issuer và lưu kết quả hoặc lịch retry phù hợp.
    internal async Task<Result> ExecuteCreateInvoiceAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        CancellationToken cancellationToken)
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

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (order.PaymentDeadline <= utcNow)
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            receipt.MarkFailed("ORDER_EXPIRED", "Payment deadline has passed; halting invoice creation.", utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
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
            return Error.Conflict("ORDER_EXPIRED", "Payment deadline has passed; halting invoice creation.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (order.Status != RentalOrderStatus.PendingPayment)
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            receipt.MarkFailed("ORDER_NOT_PENDING", "Order is no longer pending payment; halting invoice creation.", utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
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

        // Ghi nhận bắt đầu attempt trước side effect bên ngoài để worker còn dấu vết phục hồi khi process gián đoạn.
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (_invoiceIssuer == null)
        {
            return Error.Failure("INVOICE_ISSUER_UNAVAILABLE", "Invoice issuer is not configured.");
        }

        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        var invoiceResult = await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
        utcNow = _timeProvider.GetUtcNow();

        // Rẽ nhánh theo Result của bước trước; truyền lỗi hoặc dùng kết quả thành công trước khi tiếp tục luồng.
        if (invoiceResult.IsSuccess)
        {
            return Result.Success();
        }

        // Dừng nhánh không hợp lệ với mã ORDER_NOT_FOUND; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (string.Equals(invoiceResult.Error.Code, CheckoutErrors.InvoicePersistenceConflict.Code, StringComparison.OrdinalIgnoreCase))
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            _unitOfWork.DiscardChanges();

            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var reloadedOrder = await _orderRepository.GetByIdWithDetailsAsync(order.Id, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (reloadedOrder == null)
            {
                return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng sau khi nạp lại.");
            }

            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var reloadedReceipt = reloadedOrder.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.OperationId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (reloadedReceipt == null)
            {
                return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt sau khi nạp lại.");
            }

            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            DateTimeOffset reloadNow = _timeProvider.GetUtcNow();

            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (reloadedReceipt.IsSuccess && reloadedReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                return Result.Success();
            }

            // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
            if (!string.IsNullOrWhiteSpace(reloadedOrder.InvoiceRef))
            {
                return await _invoiceIssuer.EnsureInvoiceAsync(reloadedOrder, cancellationToken);
            }

            // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
            if (reloadedOrder.Status != RentalOrderStatus.PendingPayment || reloadNow >= reloadedOrder.PaymentDeadline)
            {
                // Phân loại dừng vì hết hạn hoặc order không còn chờ thanh toán để journal không bị retry mãi cùng yêu cầu không hợp lệ.
                string closeCode = reloadNow >= reloadedOrder.PaymentDeadline ? "ORDER_EXPIRED" : "ORDER_NOT_PENDING";
                string closeMsg = reloadNow >= reloadedOrder.PaymentDeadline
                    ? "Payment deadline has passed; halting invoice creation."
                    : "Order is no longer pending payment; halting invoice creation.";
                reloadedReceipt.MarkFailed(closeCode, closeMsg, reloadNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);

                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (_checkoutOperationRepo != null)
                {
                    // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
                    var op = await _checkoutOperationRepo.GetByExpectedOrderIdAsync(reloadedOrder.Id, cancellationToken);
                    // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                    if (op != null && op.InvoiceStatus != "Failed")
                    {
                        // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                        op.SetInvoiceStatus("Failed", reloadNow);
                        await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
                    }
                }
                await CommitLocalChangesAsync(reloadedOrder, cancellationToken);
                return Result.Success();
            }

            // Chuyển xung đột persistence hóa đơn thành lỗi retry sau khi đã nạp lại trạng thái, tránh ghi tiếp trên graph stale.
            var transientErr = Error.ServiceUnavailable(CheckoutErrors.InvoicePersistenceConflict.Code, "Xung đột đồng thời khi lưu thông tin hóa đơn; đã lên lịch thử lại.");
            RecordExecutionFailure(reloadedReceipt, transientErr, reloadNow, forceUncertain: false, deadline: reloadedOrder.PaymentDeadline);
            await CommitLocalChangesAsync(reloadedOrder, cancellationToken);
            return transientErr;
        }

        // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
        RecordExecutionFailure(receipt, invoiceResult.Error, utcNow, forceUncertain: false, deadline: order.PaymentDeadline);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Phân biệt receipt đã kết thúc lỗi với trường hợp được phép mở lại theo quy tắc recovery.
        if (receipt.Status == RentalOperationReceipt.StatusFailed && _checkoutOperationRepo != null)
        {
            // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
            var op = await _checkoutOperationRepo.GetByExpectedOrderIdAsync(order.Id, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (op != null)
            {
                // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                op.SetInvoiceStatus("Failed", utcNow);
                await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        return invoiceResult.Error;
    }
}
