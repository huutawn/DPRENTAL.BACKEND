#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Thực thi receipt quyết toán qua Payment sau khi đủ điều kiện nghiệp vụ.
/// Phản hồi nhận yêu cầu và sự kiện RentalSettled là hai mốc khác nhau; consumer tích hợp áp dụng kết quả quyết toán.
/// </remarks>
public sealed partial class RentalOperationExecutor
{
    // Đọc payload providerRequestJson hoặc request trực tiếp của journal; dữ liệu thiếu/hỏng trả null để caller dừng an toàn.
    internal static T? ReadStoredProviderRequest<T>(string? payloadJson) where T : class
    {
        // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
            using var doc = JsonDocument.Parse(payloadJson);
            // Đọc trường có trong payload journal; hỗ trợ đúng dạng dữ liệu đã lưu thay vì giả định mọi receipt cùng cấu trúc.
            if (doc.RootElement.TryGetProperty("providerRequestJson", out var prov) &&
                prov.ValueKind == JsonValueKind.String)
                return JsonSerializer.Deserialize<T>(prov.GetString()!, JsonOptions);
            return JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
        }
        // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
        catch (JsonException) { return null; }
    }

    // Chờ đủ khấu trừ cuối và đóng allocation, gọi Payment cùng request đã lưu; chỉ đóng receipt ACK, item chờ RentalSettled.
    internal async Task<Result> SettleRentalCoreAsync(
        RentalOrder order, RentalItem item, RentalOperationReceipt receipt, CancellationToken ct)
    {
        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Consumer đã hoàn tất item trước khi receipt kịp chốt: đóng receipt, không gọi Payment nữa.
        if (item.Status == RentalItemStatus.Completed)
        {
            // Ghi kết quả thành công của receipt; ý nghĩa hoàn tất nghiệp vụ còn phụ thuộc action và event xác nhận riêng.
            receipt.MarkSucceeded(utcNow);
            await CommitLocalChangesAsync(order, ct);
            return Result.Success();
        }

        // Dưới khóa Order: chưa đủ điều kiện thì chờ, không đốt lượt retry.
        if (item.Status != RentalItemStatus.PendingSettlement ||
            !item.FinalDeductionAmount.HasValue || !item.InventoryClosureConfirmedAt.HasValue)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogInformation("SettleRental {OpKey} chờ số khấu trừ cuối và xác nhận đóng allocation", receipt.OperationId);
            return Result.Success();
        }

        // Lưu dấu vết attempt trước khi gọi HTTP, không giữ transaction DB khi chờ Payment.
        // Nếu process dừng sau cuộc gọi, worker dùng payload/key đã lưu để tiếp tục an toàn.
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, ct);

        // Khôi phục đúng request quyết toán đã lưu, giữ nguyên số tiền/key khi worker gọi lại Payment.
        var request = ReadStoredProviderRequest<SettleRentalRequest>(receipt.PayloadJson);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request is null)
        {
            // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
            receipt.MarkFailed("CORRUPTED_REQUEST_PAYLOAD", "Payload SettleRental bị hỏng hoặc thiếu; cần đối soát.",
                utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, ct);
            return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload SettleRental bị hỏng hoặc thiếu.");
        }

        // Gọi service sở hữu dữ liệu/tác vụ bằng contract đã chuẩn bị; kết quả phải được kiểm tra trước khi chốt trạng thái local.
        var result = await _paymentClient.SettleRentalAsync(request with
        {
            SupplierAccount = await TransferRecipientResolver.FindSupplierAccountAsync(_bankAccounts, request.SupplierId, ct),
            RenterRefundAccount = TransferRecipientResolver.RefundAccountOf(order.RefundDestinationRef)
        }, ct);

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        utcNow = _timeProvider.GetUtcNow();
        // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
        if (result.IsSuccess)
        {
            // ACK phải khớp key và số tiền của request đã lưu. Phản hồi lệch được coi là
            // kết quả bất định để retry/đối soát, không được chốt thành công từ HTTP 2xx đơn thuần.
            var ack = result.Value;
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (!(string.Equals(ack.Status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(ack.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(ack.Status, "PENDING_MANUAL_REFUND", StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(ack.OperationKey, receipt.OperationId, StringComparison.OrdinalIgnoreCase) ||
                ack.FinalDeductionAmount != request.FinalDeductionAmount ||
                ack.RefundedDepositAmount != request.RefundedDepositAmount ||
                !string.Equals(ack.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            {
                // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                RecordExecutionFailure(receipt,
                    Error.Validation("INVALID_SETTLE_RESPONSE", "Phản hồi từ PaymentService không hợp lệ."),
                    utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, ct);
                return Error.Unprocessable("INVALID_SETTLE_RESPONSE", "Phản hồi quyết toán từ PaymentService không hợp lệ.");
            }

            // Accepted chỉ là ACK. Item chỉ COMPLETED khi consumer nhận RentalSettled.
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(ack, JsonOptions));
            await CommitLocalChangesAsync(order, ct);
            return Result.Success();
        }

        // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, ct);
        return result.Error;
    }
}
