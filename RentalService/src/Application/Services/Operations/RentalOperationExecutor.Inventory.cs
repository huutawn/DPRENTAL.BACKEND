#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Policies;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Phần điều phối ExtendHold và ReleaseAllocation của cùng RentalOperationExecutor.
/// Lệnh được ghi Outbox cùng receipt; kết quả thực tế được InventoryAllocationResultHandler áp dụng khi có sự kiện trả về.
/// Không đồng nhất enqueue thành công với Inventory đã gia hạn hoặc giải phóng allocation.
/// </remarks>
public sealed partial class RentalOperationExecutor
{
    // Khóa order, tìm item và receipt trước khi chạy nhánh gia hạn hold dùng chung.
    public async Task<Result> ExecuteExtendHoldAsync(
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
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

        return await ExtendHoldCoreAsync(order, item, receipt, cancellationToken);
    }

    // Dùng journal dựng lệnh gia hạn và ghi Outbox; receipt theo dõi kết quả Inventory thay vì coi enqueue là đã gia hạn.
    internal async Task<Result> ExtendHoldCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return Result.Success();
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Nếu đã hết hạn supplier xác nhận, chuyển sang nhánh bù trừ thay vì gia hạn hold.
        if (!item.SupplierConfirmationDeadline.HasValue || utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return await HandleHoldFailureAsync(order, item, receipt, "Deadline passed prior to extend hold", cancellationToken);
        }

        // Khai báo request cần khôi phục từ journal trước khi thực thi; thiếu/hỏng payload phải dừng để đối soát.
        ExtendHoldRequest request;
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
                    request = JsonSerializer.Deserialize<ExtendHoldRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    request = JsonSerializer.Deserialize<ExtendHoldRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của ExtendHold bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho ExtendHold.");
        }

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        receipt.BeginProviderAttempt(utcNow);
        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var extendCmd = new ExtendHoldCommandPayload(order.Id, item.Id, receipt.OperationId, request.NewHoldExpiresAt);
            var enqueueResult = await _inventoryCommands.EnqueueExtendHoldAsync(extendCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (enqueueResult.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await transaction.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }
        }

        // Đánh dấu tác vụ đang xử lý sau khi lập lệnh, chưa chuyển thành thành công khi chưa nhận xác nhận.
        receipt.MarkProcessing(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    // Khóa order và tìm đúng receipt/item trước khi điều phối nhả allocation.
    public async Task<Result> ExecuteReleaseAllocationAsync(
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

        return await ReleaseAllocationCoreAsync(order, item, receipt, operationKey, cancellationToken);
    }

    // Chuyển thao tác release về nhánh nội bộ dùng chung, giữ order/item/receipt mà caller đã đọc.
    internal async Task<Result> ReleaseAllocationCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        return await ReleaseAllocationInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    // Lưu attempt và enqueue release với định danh ổn định; kết quả thực tế được xác nhận ở handler Inventory.
    internal async Task<Result> ReleaseAllocationInternalAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        receipt.BeginProviderAttempt(utcNow);

        // Khai báo request cần khôi phục từ journal trước khi thực thi; thiếu/hỏng payload phải dừng để đối soát.
        ReleaseAllocationRequest request;
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
                    request = JsonSerializer.Deserialize<ReleaseAllocationRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    request = JsonSerializer.Deserialize<ReleaseAllocationRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của ReleaseAllocation bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho ReleaseAllocation.");
        }

        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var releaseCmd = new ReleaseAllocationCommandPayload(order.Id, item.Id,
                receipt.InventoryOperationKey ?? receipt.OperationId, request.Reason);
            var enqueueResult = await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (enqueueResult.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await transaction.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }
        }

        // Đánh dấu tác vụ đang xử lý sau khi lập lệnh, chưa chuyển thành thành công khi chưa nhận xác nhận.
        receipt.MarkProcessing(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    // Phân loại lỗi hold/gia hạn và chọn bù trừ release/refund theo trạng thái item, không phục hồi mù một allocation đã mất.
    private async Task<Result> HandleHoldFailureAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string reason,
        CancellationToken cancellationToken)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        item.ApplyHoldExpired(utcNow, receipt.OperationId, reason);
        receipt.MarkFailed(InventoryContractSpec.ErrorCodes.HoldExpired, reason, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);

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
                payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseKey, reason), JsonOptions));
            releaseReceipt.SetInventoryOperationKey(ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"));
            order.AddOperationReceipt(releaseReceipt);
        }

        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string refundKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
        var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (refundReceipt == null)
        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
            refundReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                refundKey,
                "RequestRefund",
                ComputeOperationKey("RequestRefund", $"{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                isSuccess: false,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusPending,
                targetItemId: item.Id,
                prerequisiteOperationId: releaseKey,
                payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, reason, refundKey, RentalRefundKinds.CancellationRefund), JsonOptions));
            order.AddOperationReceipt(refundReceipt);
        }

        // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
        RentalOrderStatusPolicy.Apply(order, utcNow, receipt.OperationId);
        order.RecordReadModelUpdate(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Ghi lệnh release khi vẫn đang giữ khóa order; trả về chưa có nghĩa Inventory đã giải phóng xong.
        await ReleaseAllocationInternalAsync(order, item, releaseReceipt, releaseKey, cancellationToken);

        return Result.Success();
    }
}
