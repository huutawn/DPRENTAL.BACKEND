#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Nhánh xác nhận allocation cho supplier accept: lưu lần thử/provider key → gọi HTTP Inventory ngoài transaction
/// → kiểm tra phản hồi → áp dụng trạng thái, receipt và event trong transaction cục bộ.
/// Timeout có thể là kết quả chưa rõ; retry phải dùng lại provider key để tránh thực hiện hai lần.
/// </remarks>
public sealed partial class RentalOperationExecutor
{
    // Gọi Inventory xác nhận allocation bằng key đã lưu, timeout hữu hạn; đọc lại trạng thái và phân biệt ACK hợp lệ với kết quả bất định.
    private async Task<Result<SupplierDecisionResponse>> ConfirmAllocationCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Đã hoàn tất thành công trước đó (idempotent replay)
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
            if (!string.IsNullOrWhiteSpace(receipt.ApiResponseJson))
            {
                // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
                try
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    var resp = JsonSerializer.Deserialize<SupplierDecisionResponse>(receipt.ApiResponseJson, JsonOptions);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (resp != null) return resp;
                }
                // Giữ nhánh bỏ qua lỗi đọc dữ liệu cũ: phần sau phải kiểm tra lại kết quả và chọn fallback/lỗi, không coi parse lỗi là dữ liệu hợp lệ.
                catch { }
            }
            return BuildSupplierResponse(order, item);
        }

        // Kiểm tra ngân sách chờ 10 giây cho cuộc gọi HTTP
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callCts.CancelAfter(TimeSpan.FromSeconds(10));

        // Retry HTTP, recovery worker và đối soát Admin dùng chung provider key đã lưu trong receipt.
        operationKey = receipt.InventoryOperationKey ?? ReadConfirmProviderKey(receipt) ?? operationKey;
        receipt.SetInventoryOperationKey(operationKey);
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
        var confirmReq = new ConfirmAllocationRequest(operationKey);
        Result<ConfirmAllocationResponse> httpResult;
        // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
        try
        {
            // Gọi service sở hữu dữ liệu/tác vụ bằng contract đã chuẩn bị; kết quả phải được kiểm tra trước khi chốt trạng thái local.
            httpResult = await _inventoryClient.ConfirmAllocationAsync(item.AllocationGroupId, confirmReq, callCts.Token);
        }
        // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Đổi lỗi timeout/exception thành lỗi Inventory để luồng giữ đúng phân loại kết quả chưa chắc chắn.
            httpResult = Error.ServiceUnavailable("Inventory.Timeout", "InventoryService không phản hồi kịp thời (timeout 10s).");
        }
        // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
        catch (Exception ex)
        {
            // Đổi lỗi timeout/exception thành lỗi Inventory để luồng giữ đúng phân loại kết quả chưa chắc chắn.
            httpResult = Error.ServiceUnavailable("Inventory.Unavailable", ex.Message);
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset afterCallNow = _timeProvider.GetUtcNow();

        // Tái nạp dữ liệu dưới lock để đảm bảo tính nhất quán (nếu có cuộc gọi song song / webhook)
        var freshOrder = await _orderRepository.GetByIdWithDetailsAsync(order.Id, cancellationToken) ?? order;
        var freshItem = freshOrder.Items.FirstOrDefault(i => i.Id == item.Id) ?? item;
        var freshReceipt = freshOrder.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.OperationId) ?? receipt;

        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (freshReceipt.IsSuccess && freshReceipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
            if (!string.IsNullOrWhiteSpace(freshReceipt.ApiResponseJson))
            {
                // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
                try
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    var resp = JsonSerializer.Deserialize<SupplierDecisionResponse>(freshReceipt.ApiResponseJson, JsonOptions);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (resp != null) return resp;
                }
                // Giữ nhánh bỏ qua lỗi đọc dữ liệu cũ: phần sau phải kiểm tra lại kết quả và chọn fallback/lỗi, không coi parse lỗi là dữ liệu hợp lệ.
                catch { }
            }
            return BuildSupplierResponse(freshOrder, freshItem);
        }

        // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
        if (httpResult.IsSuccess)
        {
            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            var validationResult = ValidateAllocationResponse(freshItem, httpResult.Value);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (validationResult.IsFailure)
            {
                // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                freshReceipt.MarkFailed(validationResult.Error.Code, validationResult.Error.Message, afterCallNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
                await CommitLocalChangesAsync(freshOrder, cancellationToken);
                return validationResult.Error;
            }

            // Áp kết quả confirm qua core chung để thay đổi state, receipt và event không bị tách thành hai đường xử lý.
            var applyResult = await ApplyConfirmAllocationResultAsync(
                freshOrder, freshReceipt, freshItem.Id, operationKey, isSuccess: true, httpResult.Value, null, null, afterCallNow, cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (applyResult.IsFailure) return applyResult.Error;

            return BuildSupplierResponse(freshOrder, freshItem);
        }
        else
        {
            // Giữ lỗi provider để phân biệt mất phản hồi với lỗi nghiệp vụ chắc chắn trước khi cập nhật receipt.
            var error = httpResult.Error;
            bool isUncertain = error.Type == ErrorType.ServiceUnavailable ||
                               error.Code.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                               error.Code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);

            // Điều kiện này quyết định có thể tiếp tục hay phải trả Error.ServiceUnavailable theo contract của use case.
            if (isUncertain)
            {
                // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                freshReceipt.MarkUncertain(error.Code, error.Message, afterCallNow);
                await CommitLocalChangesAsync(freshOrder, cancellationToken);
                return Error.ServiceUnavailable(error.Code, error.Message);
            }
            else
            {
                // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                freshReceipt.MarkFailed(error.Code, error.Message, afterCallNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
                await CommitLocalChangesAsync(freshOrder, cancellationToken);
                return error;
            }
        }
    }

    // Áp dụng kết quả confirm và outbox event trong cùng transaction; rollback toàn bộ khi bất kỳ bước ghi nào thất bại.
    public async Task<Result> ApplyConfirmAllocationResultAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        ConfirmAllocationResponse? confirmData,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Điều kiện này quyết định có thể tiếp tục hay phải trả applied.Error theo contract của use case.
            if (isSuccess)
            {
                // Áp kết quả confirm qua core chung để thay đổi state, receipt và event không bị tách thành hai đường xử lý.
                var applied = await ApplySuccessfulConfirmationAsync(order, receipt, rentalItemId,
                    operationKey, confirmData, utcNow, cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (applied.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await transaction.RollbackAsync(CancellationToken.None);
                    _unitOfWork.DiscardChanges();
                    return applied.Error;
                }
            }
            else
            {
                // Lưu phân loại lỗi/kết quả cho receipt để worker biết phải dừng, retry hay chờ đối soát.
                receipt.MarkFailed(errorCode ?? "COMMAND_REJECTED", errorMessage ?? "Rejected by Inventory", utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await CommitLocalChangesAsync(order, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await transaction.RollbackAsync(CancellationToken.None);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    // Chỉ chuyển item còn PendingSupplierConfirmation sang đã nhận; ACK đến muộn không được khôi phục item đã kết thúc.
    private async Task<Result> ApplySuccessfulConfirmationAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        ConfirmAllocationResponse? confirmData,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        // Chỉ tạo API response khi đã xác định item và nhánh xử lý; không ghi một kết quả thành công giả định trước đó.
        string? apiResponseJson = null;
        var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item != null)
        {
            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (item.Status == RentalItemStatus.PendingSupplierConfirmation)
            {
                // Áp dụng quyết định trạng thái đã qua các điều kiện của luồng; sau đó lưu history/receipt và side effect liên quan.
                item.ApplySupplierAccept(item.SupplierId, utcNow, confirmData?.ReceiptRef ?? operationKey, operationKey);
                RentalOrderStatusPolicy.Apply(order, utcNow, operationKey);
                order.RecordReadModelUpdate(utcNow);
                apiResponseJson = SerializeSupplierResponse(order, item);

                // Dựng event xác nhận từ đúng item, supplier, receipt và version sau khi quyết định đã được áp dụng.
                var outboxPayload = new RentalItemConfirmedPayload(
                    order.Id,
                    item.Id,
                    item.SupplierId,
                    utcNow,
                    confirmData?.ReceiptRef ?? operationKey,
                    order.Version);

                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var published = await _rentalEvents.EnqueueRentalItemConfirmedAsync(
                    outboxPayload,
                    order.Id.ToString("D"),
                    causationId: receipt.Id,
                    cancellationToken: cancellationToken);
                // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
                if (published.IsFailure) return published.Error;
            }
            else
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogWarning("ConfirmAllocation succeeded late for item {ItemId} in status {Status}; not restoring", item.Id, item.Status);
                apiResponseJson = SerializeSupplierResponse(order, item);
            }

            // Đóng gói snapshot/payload đang xử lý để lưu hoặc truyền theo contract; giữ dữ liệu của lần quyết định hiện tại.
            string rawPayload = confirmData != null ? JsonSerializer.Serialize(confirmData, JsonOptions) : "{}";
            receipt.MarkSucceeded(utcNow, rawPayload, apiResponseJson);
        }
        return Result.Success();
    }

    // Đối chiếu nhóm allocation, số lượng, tính duy nhất và cặp AssetId/AllocationId với mapping đã giữ cho item.
    private static Result ValidateAllocationResponse(RentalItem item, ConfirmAllocationResponse confirmData)
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(confirmData.ReceiptRef))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "ReceiptRef từ Inventory không được rỗng.");
        }

        // Dừng nhánh không hợp lệ với mã INVALID_ALLOCATION; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!string.Equals(confirmData.AllocationGroupId, item.AllocationGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "AllocationGroupId từ Inventory không khớp với đơn hàng.");
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (confirmData.Units == null || confirmData.Units.Count != item.UnitAssignments.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Số lượng units xác nhận không khớp với số lượng của đơn hàng.");
        }

        // All units must be distinct and confirmed
        var confirmedUnitIds = confirmData.Units.Select(u => u.AllocationId).ToList();
        // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
        if (confirmedUnitIds.Distinct().Count() != confirmedUnitIds.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Danh sách units từ Inventory có chứa AllocationId trùng lặp.");
        }

        // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
        foreach (var unit in item.UnitAssignments)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var match = confirmData.Units.FirstOrDefault(u => u.AllocationId == unit.AllocationId && u.AssetId == unit.AssetId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (match == null)
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Unit assignment {unit.AllocationId} không tồn tại trong kết quả từ Inventory.");
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (!string.Equals(match.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Trạng thái của unit {unit.AllocationId} không phải là Confirmed (nhận: {match.Status}).");
            }
        }

        return Result.Success();
    }

}
