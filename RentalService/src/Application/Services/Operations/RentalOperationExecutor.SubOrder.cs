using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;

namespace Dprental.RentalService.Application.Services.Operations;

/// <summary>
/// Supplier nhận hoặc từ chối cả đơn con: mọi dòng của mình trong đơn cùng một quyết định.
/// Trạng thái vẫn nằm trên từng dòng; mỗi dòng có khóa idempotency riêng suy ra từ khóa của request.
/// </summary>
public sealed partial class RentalOperationExecutor
{
    public async Task<Result<IReadOnlyList<SupplierDecisionResponse>>> ExecuteSubOrderAcceptAsync(
        Guid orderId,
        Guid subOrderId,
        Guid supplierId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        string normalizedKey = idempotencyKey.Trim();
        List<Guid> itemIds;

        // Kiểm tra mọi dòng trước khi gọi Inventory cho dòng nào, để một dòng không hợp lệ không làm đơn con bị nhận dở.
        await using (await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
            List<RentalItem> items = order is null ? [] : SubOrderItems(order, subOrderId, supplierId);
            if (order is null || items.Count == 0)
            {
                return RentalErrors.SubOrderNotFound;
            }

            DateTimeOffset utcNow = _timeProvider.GetUtcNow();
            List<(RentalItem Item, string Key)> unclaimed = [];
            foreach (RentalItem item in items)
            {
                // Dòng đã có receipt của khóa này (xong hoặc đang dở) để bước nhận từng dòng tự trả lại hoặc làm tiếp.
                string itemKey = SubOrderItemKey("SubOrderAccept", subOrderId, normalizedKey, item.Id);
                if (order.OperationReceipts.Any(r => r.OperationId == itemKey))
                {
                    continue;
                }

                Result acceptable = CheckDecidable(order, item, utcNow);
                if (acceptable.IsSuccess && !IsHoldExtended(order, item))
                {
                    acceptable = Error.Conflict("HOLD_NOT_EXTENDED", "Hold cho thiết bị chưa được gia hạn thành công (Succeeded).");
                }

                if (acceptable.IsFailure)
                {
                    return acceptable.Error;
                }

                unclaimed.Add((item, itemKey));
            }

            // Ghi phiếu "đang nhận" cho mọi dòng trong cùng một lần lưu, trước hạn và trước khi gọi Inventory:
            // dòng nào chưa chốt kịp thì expiry worker không hủy mà để recovery worker chốt nốt trong thời gian hold còn sống.
            foreach (var (item, key) in unclaimed)
            {
                order.AddOperationReceipt(NewAcceptIntent(order, item, supplierId, key, utcNow));
            }

            if (unclaimed.Count > 0)
            {
                await CommitLocalChangesAsync(order, cancellationToken);
            }

            itemIds = items.Select(i => i.Id).ToList();
        }

        // Inventory xác nhận từng dòng. Lỗi giữa chừng thì gửi lại cùng khóa sẽ làm tiếp các dòng còn thiếu.
        List<SupplierDecisionResponse> responses = [];
        foreach (Guid itemId in itemIds)
        {
            Result<SupplierDecisionResponse> accepted = await ExecuteSupplierAcceptAsync(
                orderId,
                itemId,
                supplierId,
                SubOrderItemKey("SubOrderAccept", subOrderId, normalizedKey, itemId),
                cancellationToken);
            if (accepted.IsFailure)
            {
                return accepted.Error;
            }

            responses.Add(accepted.Value);
        }

        return Result.Success<IReadOnlyList<SupplierDecisionResponse>>(responses);
    }

    public async Task<Result<IReadOnlyList<SupplierDecisionResponse>>> ExecuteSubOrderRejectAsync(
        Guid orderId,
        Guid subOrderId,
        Guid supplierId,
        string reason,
        string? note,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        string normalizedKey = idempotencyKey.Trim();
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        List<RentalItem> items = order is null ? [] : SubOrderItems(order, subOrderId, supplierId);
        if (order is null || items.Count == 0)
        {
            return RentalErrors.SubOrderNotFound;
        }

        var decisions = items
            .Select(item => (
                Item: item,
                Key: SubOrderItemKey("SubOrderReject", subOrderId, normalizedKey, item.Id),
                Fingerprint: ComputeFingerprint(new { Action = "SupplierReject", ItemId = item.Id, SupplierId = supplierId, Reason = reason, Note = note })))
            .ToList();

        // Từ chối lưu trọn trong một lần commit, nên lần gửi trước hoặc có đủ receipt cho mọi dòng hoặc không có receipt nào.
        var previous = decisions
            .Select(d => (Decision: d, Receipt: order.OperationReceipts.FirstOrDefault(r => r.OperationId == d.Key)))
            .ToList();
        if (previous.Any(p => p.Receipt is not null))
        {
            return ReplayReject(previous.Select(p => (p.Decision.Item, p.Decision.Fingerprint, p.Receipt)).ToList());
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        foreach (RentalItem item in items)
        {
            Result rejectable = CheckDecidable(order, item, utcNow);
            if (rejectable.IsFailure)
            {
                return rejectable.Error;
            }
        }

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            RentalOrderStatus previousOrderStatus = order.Status;
            List<SupplierDecisionResponse> responses = [];
            foreach (var (item, key, fingerprint) in decisions)
            {
                responses.Add(await ApplySupplierRejectAsync(order, item, supplierId, reason, note, key, fingerprint, utcNow, cancellationToken, rollup: false, enqueueEvent: false));
            }

            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);
            order.RecordReadModelUpdate(utcNow);

            foreach (var (item, key, _) in decisions)
            {
                var rejectRes = await _rentalEvents.EnqueueRentalItemRejectedAsync(
                    new RentalItemRejectedPayload(order.Id, item.Id, supplierId, utcNow, KafkaContractSpec.ReasonCodes.SupplierRejected, null, order.Version),
                    correlationId: key,
                    cancellationToken: cancellationToken);
                if (rejectRes.IsFailure)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return rejectRes.Error;
                }
            }

            if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
            {
                var orderCompletedRes = await _rentalEvents.EnqueueRentalOrderCompletedAsync(
                    new RentalOrderCompletedPayload(order.Id, order.RenterId, utcNow, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                if (orderCompletedRes.IsFailure)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderCompletedRes.Error;
                }
            }

            await CommitLocalChangesAsync(order, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return Result.Success<IReadOnlyList<SupplierDecisionResponse>>(responses);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    private static List<RentalItem> SubOrderItems(RentalOrder order, Guid subOrderId, Guid supplierId)
    {
        List<RentalItem> items = order.Items
            .Where(i => i.RentalSubOrderId == subOrderId)
            .OrderBy(i => i.Id)
            .ToList();

        // Đơn con của supplier khác trả như không tồn tại để không lộ dữ liệu.
        return items.Count > 0 && items.All(i => RentalAuthorizationPolicy.AuthorizeSupplierItem(supplierId, i).IsSuccess)
            ? items
            : [];
    }

    private static string SubOrderItemKey(string action, Guid subOrderId, string idempotencyKey, Guid itemId) =>
        ComputeOperationKey(action, $"{subOrderId}:{idempotencyKey}:{itemId}");

    /// <summary>Cùng các điều kiện mà bước nhận/từ chối từng dòng kiểm tra.</summary>
    private static Result CheckDecidable(RentalOrder order, RentalItem item, DateTimeOffset utcNow)
    {
        if (order.HasInFlightAccept(item.Id))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Yêu cầu chấp nhận đang được xử lý hoặc chờ đối soát; không thể thực hiện thao tác khác.");
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

        return Result.Success();
    }

    private static bool IsHoldExtended(RentalOrder order, RentalItem item)
    {
        string holdOpKey = ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
        var holdReceipt = order.OperationReceipts.FirstOrDefault(r =>
            r.OperationId == holdOpKey || (r.Action == "ExtendHold" && r.TargetItemId == item.Id));
        return holdReceipt is { IsSuccess: true, Status: RentalOperationReceipt.StatusSucceeded };
    }

    private static Result<IReadOnlyList<SupplierDecisionResponse>> ReplayReject(
        List<(RentalItem Item, string Fingerprint, RentalOperationReceipt? Receipt)> previous)
    {
        List<SupplierDecisionResponse> responses = [];
        foreach (var (item, fingerprint, receipt) in previous)
        {
            if (receipt is null ||
                receipt.Action != "SupplierReject" ||
                receipt.TargetItemId != item.Id ||
                !string.Equals(receipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) ||
                !receipt.IsSuccess ||
                receipt.Status != RentalOperationReceipt.StatusSucceeded)
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            SupplierDecisionResponse? stored = null;
            try
            {
                stored = string.IsNullOrWhiteSpace(receipt.ApiResponseJson)
                    ? null
                    : JsonSerializer.Deserialize<SupplierDecisionResponse>(receipt.ApiResponseJson, JsonOptions);
            }
            catch (JsonException)
            {
            }

            if (stored is null)
            {
                return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
            }

            responses.Add(stored);
        }

        return Result.Success<IReadOnlyList<SupplierDecisionResponse>>(responses);
    }
}
