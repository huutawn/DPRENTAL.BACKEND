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

public sealed partial class RentalOperationExecutor
{
    public async Task<Result<SupplierDecisionResponse>> ExecuteSupplierAcceptAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string structuredFingerprint = ComputeFingerprint(new { Action = "SupplierAccept", ItemId = itemId, SupplierId = supplierId });
        string legacyFingerprint = ComputeFingerprint($"accept:{itemId}:{supplierId}");

        // 1. Replay check on existing receipt with same key
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            bool isFingerprintMatch = string.Equals(existingReceipt.Fingerprint, structuredFingerprint, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(existingReceipt.Fingerprint, legacyFingerprint, StringComparison.OrdinalIgnoreCase);

            if (existingReceipt.Action != "SupplierAccept" ||
                existingReceipt.TargetItemId != itemId ||
                !isFingerprintMatch)
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            if (existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                if (string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
                {
                    return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal bị hỏng.");
                }
                try
                {
                    var storedResp = JsonSerializer.Deserialize<SupplierDecisionResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    if (storedResp != null) return storedResp;
                }
                catch
                {
                    return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                }
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            if (existingReceipt.ErrorClassification == RentalOperationReceipt.ClassificationUncertainExhausted)
            {
                return Error.Conflict("UNCERTAIN_EXHAUSTED", "Yêu cầu trước đó đã hết ngân sách thử lại nhưng chưa rõ kết quả; cần can thiệp Admin replay.");
            }

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

        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        if (existingReceipt == null && item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }
        // 4. ExtendHold must be Succeeded
        string holdOpKey = ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
        var holdReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == holdOpKey || (r.Action == "ExtendHold" && r.TargetItemId == item.Id));
        if (holdReceipt == null || !holdReceipt.IsSuccess || holdReceipt.Status != RentalOperationReceipt.StatusSucceeded)
        {
            return Error.Conflict("HOLD_NOT_EXTENDED", "Hold cho thiết bị chưa được gia hạn thành công (Succeeded).");
        }

        // 5. Claim intent in journal before HTTP call
        var receipt = existingReceipt ?? NewAcceptIntent(order, item, supplierId, normalizedKey, utcNow);

        if (existingReceipt == null)
        {
            order.AddOperationReceipt(receipt);
        }
        else
        {
            receipt.MarkProcessing(utcNow);
        }
        // 6. Execute ConfirmAllocation core
        string providerKey = receipt.InventoryOperationKey ?? ReadConfirmProviderKey(receipt) ??
            ComputeOperationKey("ConfirmAllocation", $"{order.Id}:{item.Id}:{normalizedKey}");
        receipt.SetInventoryOperationKey(providerKey);
        return await ConfirmAllocationCoreAsync(order, item, receipt, providerKey, supplierId, isReconciliation: false, cancellationToken);
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

    private static string? ReadConfirmProviderKey(RentalOperationReceipt receipt)
    {
        try
        {
            var command = JsonSerializer.Deserialize<RentalOperationCommand>(receipt.PayloadJson ?? "", JsonOptions);
            var request = JsonSerializer.Deserialize<ConfirmAllocationRequest>(command?.ProviderRequestJson ?? "", JsonOptions);
            return request?.OperationKey;
        }
        catch (JsonException) { return null; }
    }

    public async Task<Result<SupplierDecisionResponse>> ExecuteSupplierRejectAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string reason,
        string? note,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string structuredFingerprint = ComputeFingerprint(new { Action = "SupplierReject", ItemId = itemId, SupplierId = supplierId, Reason = reason, Note = note });
        string legacyFingerprint = ComputeFingerprint($"reject:{itemId}:{reason}:{note}");
        string fingerprint = structuredFingerprint;
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            bool isFingerprintMatch = string.Equals(existingReceipt.Fingerprint, structuredFingerprint, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(existingReceipt.Fingerprint, legacyFingerprint, StringComparison.OrdinalIgnoreCase);

            if (existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                if (existingReceipt.Action == "SupplierReject" &&
                    existingReceipt.TargetItemId == itemId &&
                    isFingerprintMatch)
                {
                    if (string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal bị hỏng.");
                    }
                    try
                    {
                        var storedResp = JsonSerializer.Deserialize<SupplierDecisionResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                        if (storedResp != null) return storedResp;
                    }
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

        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_ALREADY_DECIDED", "Mục thuê đã được quyết định với khóa idempotency khác.");
        }

        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        if (item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }

        var response = await ApplySupplierRejectAsync(order, item, supplierId, reason, note, normalizedKey, fingerprint, utcNow, cancellationToken, rollup: true);
        order.RecordReadModelUpdate(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        return response;
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
        bool rollup = true)
    {
        item.ApplySupplierReject(supplierId, utcNow, reason, note, normalizedKey);
        if (rollup)
        {
            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);
        }

        var response = BuildSupplierResponse(order, item);
        string apiResponseJson = JsonSerializer.Serialize(response, JsonOptions);

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

        await ScheduleReleaseAndRefundAsync(order, item, supplierId, reason, utcNow, cancellationToken);

        var notifyEnvelope = new KafkaMessageEnvelope<RentalItemRejectedPayload>(
            OutboxMessageIdGenerator.ComputeDeterministicId(
                KafkaContractSpec.Producers.RentalService,
                KafkaContractSpec.EventTypes.RentalItemRejected,
                normalizedKey),
            KafkaContractSpec.EventTypes.RentalItemRejected,
            1,
            KafkaContractSpec.Producers.RentalService,
            order.Id.ToString("D"),
            utcNow,
            normalizedKey,
            null,
            new RentalItemRejectedPayload(order.Id, item.Id, supplierId, utcNow, reason, note));

        var outbox = new RentalOutboxMessage(
            notifyEnvelope.MessageId,
            _rentalEventsTopic,
            order.Id.ToString("D"),
            KafkaContractSpec.EventTypes.RentalItemRejected,
            JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
            utcNow);

        await _outboxRepository.AddAsync(outbox, cancellationToken);

        return response;
    }

    public async Task<Result> ExecuteReconcileAcceptAsync(
        Guid orderId,
        Guid itemId,
        string acceptOperationKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        var acceptReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == acceptOperationKey);
        if (acceptReceipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy receipt accept.");

        if (acceptReceipt.IsSuccess && acceptReceipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        // Invariant: Do NOT restore Rejected or Expired items to AwaitingHandover!
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            _logger.LogWarning("Cannot reconcile accept for item {ItemId} in terminal status {Status}", item.Id, item.Status);
            return Error.Conflict("ITEM_IN_TERMINAL_STATE", $"Mục thuê đã ở trạng thái {item.Status}; không thể khôi phục thành AwaitingHandover.");
        }

        return await ConfirmAllocationCoreAsync(order, item, acceptReceipt, acceptOperationKey, item.SupplierId, isReconciliation: true, cancellationToken);
    }

    private async Task<Result<SupplierDecisionResponse>> ConfirmAllocationCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        Guid supplierId,
        bool isReconciliation,
        CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        receipt.BeginProviderAttempt(utcNow);
        {
            var confirmCmd = new ConfirmAllocationCommandPayload(order.Id, item.Id, operationKey);
            var enqueueResult = await _inventoryCommands.EnqueueConfirmAllocationAsync(confirmCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
            if (enqueueResult.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }
        }

        receipt.MarkProcessing(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BuildSupplierResponse(order, item);
    }

    private static Result ValidateAllocationResponse(RentalItem item, ConfirmAllocationResponse confirmData)
    {
        if (string.IsNullOrWhiteSpace(confirmData.ReceiptRef))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "ReceiptRef từ Inventory không được rỗng.");
        }

        if (!string.Equals(confirmData.AllocationGroupId, item.AllocationGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "AllocationGroupId từ Inventory không khớp với đơn hàng.");
        }

        if (confirmData.Units == null || confirmData.Units.Count != item.UnitAssignments.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Số lượng units xác nhận không khớp với số lượng của đơn hàng.");
        }

        // All units must be distinct and confirmed
        var confirmedUnitIds = confirmData.Units.Select(u => u.AllocationId).ToList();
        if (confirmedUnitIds.Distinct().Count() != confirmedUnitIds.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Danh sách units từ Inventory có chứa AllocationId trùng lặp.");
        }

        foreach (var unit in item.UnitAssignments)
        {
            var match = confirmData.Units.FirstOrDefault(u => u.AllocationId == unit.AllocationId && u.AssetId == unit.AssetId);
            if (match == null)
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Unit assignment {unit.AllocationId} không tồn tại trong kết quả từ Inventory.");
            }

            if (!string.Equals(match.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Trạng thái của unit {unit.AllocationId} không phải là Confirmed (nhận: {match.Status}).");
            }
        }

        return Result.Success();
    }

    /// <summary>Response API của supplier accept, lưu vào phiếu để gửi lại cùng khóa trả đúng kết quả đã chốt.</summary>
    internal static string SerializeSupplierResponse(RentalOrder order, RentalItem item) =>
        JsonSerializer.Serialize(BuildSupplierResponse(order, item), JsonOptions);

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

    public async Task ScheduleReleaseAndRefundAsync(
        RentalOrder order,
        RentalItem item,
        Guid actorId,
        string reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        string releaseOpKey = ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
        var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
        if (releaseReceipt == null)
        {
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

        if (_inventoryCommands != null)
        {
            var releaseCmdPayload = new ReleaseAllocationCommandPayload(order.Id, item.Id, releaseOpKey, reason);
            await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmdPayload, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
        }

        string refundOpKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
        var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey);
        if (refundReceipt == null)
        {
            var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
            var refundReq = new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, reason, refundOpKey);
            var refundCmd = new RentalOperationCommand(
                1,
                "RequestRefund",
                order.Id,
                item.Id,
                refundOpKey,
                null,
                actorId,
                JsonSerializer.Serialize(refundReq, JsonOptions));

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
