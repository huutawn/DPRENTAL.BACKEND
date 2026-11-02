using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Policies;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Xử lý nhận/từ chối item và đối soát supplier accept trong cùng executor.
/// Accept lưu intent trước khi xác nhận allocation; reject điều phối lịch release/refund và event.
/// Order lock và fingerprint ngăn quyết định đồng thời hoặc dùng cùng idempotency key với nội dung khác.
/// </remarks>
public sealed partial class RentalOperationExecutor
{
    // Khóa order, xác thực supplier/trạng thái và journal; lưu intent trước khi confirm Inventory để phục hồi được khi mất phản hồi.
    public async Task<Result<SupplierDecisionResponse>> ExecuteSupplierAcceptAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // A sub-order request persists its intents before releasing and reacquiring this lock.
        // Reload the aggregate so a concurrent confirmation cannot leave stale tracked versions here.
        _unitOfWork.DiscardChanges();
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string structuredFingerprint = ComputeFingerprint(new { Action = "SupplierAccept", ItemId = itemId, SupplierId = supplierId });
        string legacyFingerprint = ComputeFingerprint($"accept:{itemId}:{supplierId}");

        // 1. Replay check on existing receipt with same key
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReceipt != null)
        {
            // Đối chiếu các dạng fingerprint mà implementation hiện còn hỗ trợ, giữ hành vi replay của receipt đã được lưu trước đó.
            bool isFingerprintMatch = string.Equals(existingReceipt.Fingerprint, structuredFingerprint, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(existingReceipt.Fingerprint, legacyFingerprint, StringComparison.OrdinalIgnoreCase);

            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (existingReceipt.Action != "SupplierAccept" ||
                existingReceipt.TargetItemId != itemId ||
                !isFingerprintMatch)
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
                if (string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
                {
                    return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal bị hỏng.");
                }
                // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
                try
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    var storedResp = JsonSerializer.Deserialize<SupplierDecisionResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (storedResp != null) return storedResp;
                }
                // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
                catch
                {
                    return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                }
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            // Dừng nhánh không hợp lệ với mã UNCERTAIN_EXHAUSTED; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (existingReceipt.ErrorClassification == RentalOperationReceipt.ClassificationUncertainExhausted)
            {
                return Error.Conflict("UNCERTAIN_EXHAUSTED", "Yêu cầu trước đó đã hết ngân sách thử lại nhưng chưa rõ kết quả; cần can thiệp Admin replay.");
            }

            // Kiểm tra lịch và ngân sách retry để worker không thực thi quá sớm hoặc lặp vô hạn.
            if (existingReceipt.NextRetryAt.HasValue && existingReceipt.NextRetryAt.Value > utcNow)
            {
                return Error.ServiceUnavailable("RETRY_SCHEDULED", "Yêu cầu đang chờ thời điểm thử lại tự động theo lịch.");
            }
        }
        // 2. DECISION_IN_PROGRESS check: check if any in-flight accept intent exists with different key
        if (existingReceipt == null && order.HasInFlightAccept(itemId))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Yêu cầu chấp nhận đang được xử lý hoặc chờ đối soát; không thể thực hiện thao tác khác.");
        }

        // 3. Current state check
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_ALREADY_DECIDED", "Mục thuê đã được quyết định với khóa idempotency khác.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (existingReceipt == null && item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }
        // 4. ExtendHold must be Succeeded
        string holdOpKey = ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
        var holdReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == holdOpKey || (r.Action == "ExtendHold" && r.TargetItemId == item.Id));
        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (holdReceipt == null || !holdReceipt.IsSuccess || holdReceipt.Status != RentalOperationReceipt.StatusSucceeded)
        {
            return Error.Conflict("HOLD_NOT_EXTENDED", "Hold cho thiết bị chưa được gia hạn thành công (Succeeded).");
        }

        // 5. Claim intent in journal before HTTP call
        var receipt = existingReceipt ?? NewAcceptIntent(order, item, supplierId, normalizedKey, utcNow);

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (existingReceipt == null)
        {
            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(receipt);
        }
        else
        {
            // Đánh dấu tác vụ đang xử lý sau khi lập lệnh, chưa chuyển thành thành công khi chưa nhận xác nhận.
            receipt.MarkProcessing(utcNow);
        }
        // 6. Execute ConfirmAllocation core
        string providerKey = ComputeOperationKey("ConfirmAllocation", $"{order.Id}:{item.Id}:{normalizedKey}");
        return await ConfirmAllocationCoreAsync(order, item, receipt, providerKey, cancellationToken);
    }

    /// <summary>
    /// Phiếu "đang nhận" (Processing) ghi vào journal trước khi gọi Inventory ConfirmAllocation.
    /// </summary>
    private static RentalOperationReceipt NewAcceptIntent(
        RentalOrder order,
        RentalItem item,
        Guid supplierId,
        string operationKey,
        DateTimeOffset utcNow)
    {
        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string providerKey = ComputeOperationKey("ConfirmAllocation", $"{order.Id}:{item.Id}:{operationKey}");
        var confirmReq = new ConfirmAllocationRequest(providerKey);
        var command = new RentalOperationCommand(
            1,
            "SupplierAccept",
            order.Id,
            item.Id,
            providerKey,
            item.AllocationGroupId,
            supplierId,
            JsonSerializer.Serialize(confirmReq, JsonOptions));

        // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            operationKey,
            "SupplierAccept",
            ComputeFingerprint(new { Action = "SupplierAccept", ItemId = item.Id, SupplierId = supplierId }),
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusProcessing,
            targetItemId: item.Id,
            payloadJson: JsonSerializer.Serialize(command, JsonOptions));
        receipt.SetInventoryOperationKey(providerKey);
        return receipt;
    }

    // Đọc key confirm từ payload journal; không tự thay key khi phục hồi tác vụ đang có thể đã thành công ở Inventory.
    private static string? ReadConfirmProviderKey(RentalOperationReceipt receipt)
    {
        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
            var command = JsonSerializer.Deserialize<RentalOperationCommand>(receipt.PayloadJson ?? "", JsonOptions);
            var request = JsonSerializer.Deserialize<ConfirmAllocationRequest>(command?.ProviderRequestJson ?? "", JsonOptions);
            return request?.OperationKey;
        }
        // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
        catch (JsonException) { return null; }
    }

    // Khóa order, kiểm tra replay/quyền/deadline và accept còn treo trước khi từ chối item cùng tác vụ release/refund.
    public async Task<Result<SupplierDecisionResponse>> ExecuteSupplierRejectAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string reason,
        string? note,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string structuredFingerprint = ComputeFingerprint(new { Action = "SupplierReject", ItemId = itemId, SupplierId = supplierId, Reason = reason, Note = note });
        string legacyFingerprint = ComputeFingerprint($"reject:{itemId}:{reason}:{note}");
        string fingerprint = structuredFingerprint;
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReceipt != null)
        {
            // Đối chiếu các dạng fingerprint mà implementation hiện còn hỗ trợ, giữ hành vi replay của receipt đã được lưu trước đó.
            bool isFingerprintMatch = string.Equals(existingReceipt.Fingerprint, structuredFingerprint, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(existingReceipt.Fingerprint, legacyFingerprint, StringComparison.OrdinalIgnoreCase);

            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
                if (existingReceipt.Action == "SupplierReject" &&
                    existingReceipt.TargetItemId == itemId &&
                    isFingerprintMatch)
                {
                    // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
                    if (string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal bị hỏng.");
                    }
                    // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
                    try
                    {
                        // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                        var storedResp = JsonSerializer.Deserialize<SupplierDecisionResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                        if (storedResp != null) return storedResp;
                    }
                    // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
                    catch
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                    }
                }
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }
        }

        // DECISION_IN_PROGRESS check: in-flight accept blocks reject
        if (order.HasInFlightAccept(itemId))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Yêu cầu chấp nhận đang được xử lý hoặc chờ đối soát; không thể từ chối mục thuê.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_ALREADY_DECIDED", "Mục thuê đã được quyết định với khóa idempotency khác.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
            RentalOrderStatus previousOrderStatus = order.Status;
            var response = await ApplySupplierRejectAsync(order, item, supplierId, reason, note, normalizedKey, fingerprint, utcNow, cancellationToken, rollup: true, enqueueEvent: false);
            order.RecordReadModelUpdate(utcNow);

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var rejectEventRes = await _rentalEvents.EnqueueRentalItemRejectedAsync(
                new RentalItemRejectedPayload(order.Id, item.Id, supplierId, utcNow, KafkaContractSpec.ReasonCodes.SupplierRejected, null, order.Version),
                correlationId: normalizedKey,
                cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (rejectEventRes.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return rejectEventRes.Error;
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

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Rejected && order.Status == RentalOrderStatus.Rejected)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderRejected = await _rentalEvents.EnqueueRentalOrderRejectedAsync(
                    new RentalOrderRejectedPayload(order.Id, order.RenterId, utcNow, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (orderRejected.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderRejected.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Expired && order.Status == RentalOrderStatus.Expired)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderExpired = await _rentalEvents.EnqueueRentalOrderExpiredAsync(
                    new RentalOrderExpiredPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, order.Version),
                    correlationId: normalizedKey,
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

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
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
                        OrderChangeTypes.SupplierRejected,
                        utcNow),
                    cancellationToken);

                // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == supplierId);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (subOrder != null)
                {
                    // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                    await _notifier.NotifySupplierSubOrderChangedAsync(
                        supplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder.Id,
                            order.Version,
                            OrderChangeTypes.SupplierRejected,
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

    /// <summary>
    /// Chuyển một dòng sang Rejected, ghi receipt, lên lịch nhả lịch và hoàn tiền, thêm outbox; chưa commit.
    /// </summary>
    private async Task<SupplierDecisionResponse> ApplySupplierRejectAsync(
        RentalOrder order,
        RentalItem item,
        Guid supplierId,
        string reason,
        string? note,
        string normalizedKey,
        string fingerprint,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken,
        bool rollup = true,
        bool enqueueEvent = true)
    {
        // Áp dụng quyết định trạng thái đã qua các điều kiện của luồng; sau đó lưu history/receipt và side effect liên quan.
        item.ApplySupplierReject(supplierId, utcNow, reason, note, normalizedKey);
        // Caller xử lý cả suborder có thể dồn rollup tới cuối; nhánh xử lý item đơn lẻ phải tổng hợp order ngay tại đây.
        if (rollup)
        {
            // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);
        }

        // Dựng response quyết định supplier từ dữ liệu đã áp dụng để lưu/đáp lại đúng kết quả của thao tác.
        var response = BuildSupplierResponse(order, item);
        string apiResponseJson = JsonSerializer.Serialize(response, JsonOptions);

        // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
        var rejectReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            "SupplierReject",
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            targetItemId: item.Id,
            apiResponseJson: apiResponseJson);
        order.AddOperationReceipt(rejectReceipt);

        // Lập tác vụ phụ thuộc cho luồng hiện tại; việc lập lịch chưa có nghĩa Inventory hoặc Payment đã thực hiện xong.
        await ScheduleReleaseAndRefundAsync(order, item, supplierId, reason, utcNow, cancellationToken);

        // Điều kiện này quyết định có thể tiếp tục hay phải trả new theo contract của use case.
        if (enqueueEvent)
        {
            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var rejectEventRes = await _rentalEvents.EnqueueRentalItemRejectedAsync(
                new RentalItemRejectedPayload(order.Id, item.Id, supplierId, utcNow, KafkaContractSpec.ReasonCodes.SupplierRejected, null, order.Version),
                correlationId: normalizedKey,
                cancellationToken: cancellationToken);
            // Trả lỗi từ bước kiểm tra vừa chạy; không thực hiện phần ghi hoặc gọi provider tiếp theo khi điều kiện chưa đạt.
            if (rejectEventRes.IsFailure)
            {
                throw new InvalidOperationException($"Failed to enqueue RentalItemRejected event: {rejectEventRes.Error.Message}");
            }
        }

        return response;
    }

    // Đối soát receipt accept đã lưu bằng cùng confirm key; tránh kết luận timeout đồng nghĩa Inventory chưa xác nhận.
    public async Task<Result> ExecuteReconcileAcceptAsync(
        Guid orderId,
        Guid itemId,
        string acceptOperationKey,
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
        var acceptReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == acceptOperationKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (acceptReceipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy receipt accept.");

        // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
        if (acceptReceipt.IsSuccess && acceptReceipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        // Invariant: Do NOT restore Rejected or Expired items to AwaitingHandover!
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogWarning("Cannot reconcile accept for item {ItemId} in terminal status {Status}", item.Id, item.Status);
            return Error.Conflict("ITEM_IN_TERMINAL_STATE", $"Mục thuê đã ở trạng thái {item.Status}; không thể khôi phục thành AwaitingHandover.");
        }

        return await ConfirmAllocationCoreAsync(order, item, acceptReceipt, acceptOperationKey, cancellationToken);
    }

    /// <summary>Response API của supplier accept, lưu vào phiếu để gửi lại cùng khóa trả đúng kết quả đã chốt.</summary>
    internal static string SerializeSupplierResponse(RentalOrder order, RentalItem item) =>
        JsonSerializer.Serialize(BuildSupplierResponse(order, item), JsonOptions);

    // Dựng response quyết định từ item/order hiện tại và các mốc supplier đã được ghi nhận.
    private static SupplierDecisionResponse BuildSupplierResponse(RentalOrder order, RentalItem item)
    {
        return new SupplierDecisionResponse(
            item.Id,
            order.Id,
            order.OrderCode,
            item.Status.ToString(),
            item.SupplierDecisionAt,
            item.SupplierDecisionBy,
            item.SupplierDecisionReason,
            item.SupplierDecisionNote);
    }

    // Lập journal nhả allocation và hoàn tiền theo item bằng key ổn định; lưu quan hệ phụ thuộc để worker không chạy sai thứ tự.
    public async Task ScheduleReleaseAndRefundAsync(
        RentalOrder order,
        RentalItem item,
        Guid actorId,
        string reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string releaseOpKey = ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
        var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (releaseReceipt == null)
        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var releaseReq = new ReleaseAllocationRequest(releaseOpKey, reason);
            var relCmd = new RentalOperationCommand(
                1,
                "ReleaseAllocation",
                order.Id,
                item.Id,
                releaseOpKey,
                item.AllocationGroupId,
                actorId,
                JsonSerializer.Serialize(releaseReq, JsonOptions));

            // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
            releaseReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                releaseOpKey,
                "ReleaseAllocation",
                ComputeFingerprint($"ReleaseAllocation:{item.AllocationGroupId}"),
                isSuccess: false,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusPending,
                targetItemId: item.Id,
                payloadJson: JsonSerializer.Serialize(relCmd, JsonOptions));
            order.AddOperationReceipt(releaseReceipt);
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (_inventoryCommands != null)
        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var releaseCmdPayload = new ReleaseAllocationCommandPayload(order.Id, item.Id, releaseOpKey, reason);
            await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmdPayload, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
        }

        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string refundOpKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
        var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (refundReceipt == null)
        {
            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
            var refundReq = new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, reason, refundOpKey, RentalRefundKinds.CancellationRefund);
            var refundCmd = new RentalOperationCommand(
                1,
                "RequestRefund",
                order.Id,
                item.Id,
                refundOpKey,
                null,
                actorId,
                JsonSerializer.Serialize(refundReq, JsonOptions));

            // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
            refundReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                refundOpKey,
                "RequestRefund",
                ComputeFingerprint($"RequestRefund:{order.Id}:{item.RentalAmount + item.DepositAmount}"),
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
    }
}
