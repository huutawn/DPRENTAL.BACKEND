using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;

namespace Dprental.RentalService.Application.Kafka.Consumers;

/// <summary>
/// Áp dụng xác nhận trả máy từ Inventory; chỉ phát lệnh không đủ để đóng assignment.
/// Khi receipt đã Succeeded hoặc Failed, từ chối kết quả mâu thuẫn để không ghi đè kết quả đã chốt.
/// </summary>
/// <remarks>
/// Được dispatcher gọi trong transaction xử lý Inbox sau khi có kết quả ApplyReturnedUnits từ Inventory.
/// Đối chiếu từng allocation/asset/condition với lệnh gốc; máy hỏng phải có bằng chứng trạng thái được bảo vệ.
/// Chỉ kết quả hợp lệ mới ghi đóng allocation và phát RentalItemReturned khi hoàn tất; kết quả mâu thuẫn bị từ chối.
/// </remarks>
public sealed class InventoryReturnResultHandler(
    IRentalOrderRepository orders,
    TimeProvider clock,
    IRentalEventProducer rentalEvents)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Áp dụng ReturnedUnitsApplied: xác minh mọi unit trước khi ghi kết quả đóng allocation.
    // Chỉ khi các unit đã trả đều được Inventory xác nhận đóng mới ghi InventoryClosure và event hoàn tất trả.
    public async Task<Result> ApplyStagedAsync(string json, CancellationToken ct = default)
    {
        KafkaMessageEnvelope<ReturnedUnitsAppliedPayload>? envelope;
        try { envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<ReturnedUnitsAppliedPayload>>(json, JsonOptions); }
        catch (JsonException) { return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result is not valid JSON."); }
        if (!IsValidEnvelope(envelope, KafkaContractSpec.EventTypes.ReturnedUnitsApplied) || envelope!.Payload is null)
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result envelope is invalid.");

        var result = envelope.Payload;
        if (!string.Equals(envelope.AggregateId, result.RentalOrderId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return Error.Validation("AGGREGATE_PAYLOAD_MISMATCH", "AggregateId must equal payload RentalOrderId.");
        if (string.IsNullOrWhiteSpace(result.ReceiptRef) || string.IsNullOrWhiteSpace(result.AllocationGroupId) ||
            string.IsNullOrWhiteSpace(result.OperationKey) || result.Units is null || result.Units.Count == 0 ||
            result.Units.Select(u => u.AllocationId).Distinct().Count() != result.Units.Count)
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result payload is invalid.");

        var targetResult = await ValidateTargetAsync(result.RentalOrderId, result.RentalItemId, result.AllocationGroupId,
            result.ReturnRecordId, result.OperationKey, ct);
        if (targetResult.IsFailure) return targetResult.Error;
        var (order, item, receipt, command) = targetResult.Value;
        if (receipt.Status == RentalOperationReceipt.StatusSucceeded)
            return !string.IsNullOrWhiteSpace(receipt.ApiResponseJson) &&
                   CanonicalJson.AreEquivalent(receipt.ApiResponseJson, JsonSerializer.Serialize(result, JsonOptions))
                ? Result.Success()
                : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Another Inventory result was already applied.");
        if (receipt.Status == RentalOperationReceipt.StatusFailed)
            return Error.Conflict("INVENTORY_RESULT_CONFLICT", "Return operation was already marked as failed; cannot apply late result.");
        if (command.Units.Count != result.Units.Count)
            return Error.Validation("RETURN_RESULT_MISMATCH", "Inventory result units count differs from the command.");

        foreach (var returned in result.Units)
        {
            var requested = command.Units.SingleOrDefault(u => u.AllocationId == returned.AllocationId);
            var assignment = item.UnitAssignments.SingleOrDefault(a => a.AllocationId == returned.AllocationId);
            if (requested is null || assignment is null || assignment.AssetId != returned.AssetId ||
                assignment.ReturnRecordId != result.ReturnRecordId || returned.AllocationClosedAt == default ||
                (requested.Condition == "DAMAGED" && (assignment.ReturnCondition != Common.Enums.UnitCondition.Damaged ||
                    string.IsNullOrWhiteSpace(returned.DamageDispositionRef) || !IsProtected(returned.Status))) ||
                (requested.Condition == "NORMAL" && (assignment.ReturnCondition != Common.Enums.UnitCondition.Normal ||
                    returned.DamageDispositionRef is not null || returned.Status is not ("RELEASED" or "CLOSED"))) ||
                requested.Condition is not ("NORMAL" or "DAMAGED"))
            {
                return Error.Validation("INVALID_INVENTORY_RESULT", "Inventory result has an invalid unit.");
            }

            if (assignment.AllocationClosedAt.HasValue &&
                (assignment.AllocationClosedAt != returned.AllocationClosedAt || assignment.InventoryDispositionRef != returned.DamageDispositionRef))
            {
                return Error.Conflict("INVENTORY_CLOSURE_CONFLICT", "Assignment closure differs from the recorded result.");
            }

            if (assignment.ReturnCondition == Common.Enums.UnitCondition.Damaged &&
                assignment.InventoryDispositionRef is not null && assignment.InventoryDispositionRef != returned.DamageDispositionRef)
            {
                return Error.Conflict("DAMAGE_DISPOSITION_CONFLICT", "Damage disposition differs from the recorded result.");
            }
        }

        DateTimeOffset now = clock.GetUtcNow();
        bool inventoryWasClosed = item.InventoryClosureConfirmedAt.HasValue;
        foreach (var returned in result.Units)
        {
            var assignment = item.UnitAssignments.Single(a => a.AllocationId == returned.AllocationId);
            if (assignment.ReturnCondition == Common.Enums.UnitCondition.Damaged)
            {
                assignment.RecordInventoryDisposition(returned.DamageDispositionRef!);
            }
            assignment.RecordAllocationClosed(returned.AllocationClosedAt.ToUniversalTime(), returned.DamageDispositionRef);
        }

        if (item.AreAllUnitsReturned && item.UnitAssignments.All(a => a.AllocationClosedAt.HasValue))
        {
            item.SetInventoryClosure(result.ReceiptRef, now);
        }

        receipt.MarkSucceeded(now, null, JsonSerializer.Serialize(result, JsonOptions));
        RentalOrderStatusPolicy.Apply(order, now, result.ReceiptRef);
        order.RecordReadModelUpdate(now);
        if (!inventoryWasClosed && item.InventoryClosureConfirmedAt.HasValue)
        {
            var published = await rentalEvents.EnqueueRentalItemReturnedAsync(
                new RentalItemReturnedPayload(order.Id, item.Id, order.RenterId, item.SupplierId,
                    item.EquipmentListingId, item.Quantity, item.ReturnsCompletedAt ?? now, order.Version),
                correlationId: order.Id.ToString("D"), causationId: envelope.MessageId, cancellationToken: ct);
            if (published.IsFailure) return published.Error;
        }
        await orders.UpdateAsync(order, ct);
        return Result.Success();
    }

    // Áp dụng ReturnedUnitsRejected vào receipt tương ứng; không cho kết quả từ chối ghi đè tác vụ đã thành công.
    // Bản tin lặp cùng lỗi được chấp nhận để việc nhận lại không tạo thay đổi mới.
    public async Task<Result> RejectStagedAsync(string json, CancellationToken ct = default)
    {
        KafkaMessageEnvelope<ReturnedUnitsRejectedPayload>? envelope;
        try { envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<ReturnedUnitsRejectedPayload>>(json, JsonOptions); }
        catch (JsonException) { return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result is not valid JSON."); }
        if (!IsValidEnvelope(envelope, KafkaContractSpec.EventTypes.ReturnedUnitsRejected) || envelope!.Payload is null)
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result envelope is invalid.");
        var result = envelope.Payload;
        if (!string.Equals(envelope.AggregateId, result.RentalOrderId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return Error.Validation("AGGREGATE_PAYLOAD_MISMATCH", "AggregateId must equal payload RentalOrderId.");
        var targetResult = await ValidateTargetAsync(result.RentalOrderId, result.RentalItemId, result.AllocationGroupId,
            result.ReturnRecordId, result.OperationKey, ct);
        if (targetResult.IsFailure) return targetResult.Error;
        var (order, _, receipt, _) = targetResult.Value;
        if (receipt.Status == RentalOperationReceipt.StatusSucceeded)
            return Error.Conflict("INVENTORY_RESULT_CONFLICT", "Return operation already succeeded; rejection cannot override it.");
        if (receipt.Status == RentalOperationReceipt.StatusFailed)
            return string.Equals(receipt.ErrorCode, result.ErrorCode, StringComparison.OrdinalIgnoreCase)
                ? Result.Success()
                : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Return operation already failed with different error code.");
        receipt.MarkFailed(result.ErrorCode, result.ErrorMessage, clock.GetUtcNow(),
            RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        await orders.UpdateAsync(order, ct);
        return Result.Success();
    }

    private static bool IsValidEnvelope<T>(KafkaMessageEnvelope<T>? envelope, string type) =>
        envelope is not null && envelope.MessageId != Guid.Empty && envelope.Version == KafkaContractSpec.CurrentEnvelopeVersion &&
        string.Equals(envelope.MessageType, type, StringComparison.Ordinal) &&
        string.Equals(envelope.Producer, KafkaContractSpec.Producers.InventoryService, StringComparison.Ordinal) &&
        envelope.Payload is not null;

    private async Task<Result<(RentalOrder Order, RentalItem Item, RentalOperationReceipt Receipt, ApplyReturnedUnitsCommandPayload Command)>> ValidateTargetAsync(
        Guid orderId,
        Guid itemId,
        string allocationGroupId,
        Guid returnRecordId,
        string operationKey,
        CancellationToken ct)
    {
        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Error.NotFound("RETURN_ORDER_NOT_FOUND", "Return order was not found.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || item.AllocationGroupId != allocationGroupId || !item.ReturnRecords.Any(r => r.Id == returnRecordId))
        {
            return Error.Validation("INVALID_RETURN_TARGET", "Inventory result does not match the return.");
        }

        string operationId = $"return:{returnRecordId:N}:apply";
        var receipt = order.OperationReceipts.FirstOrDefault(r =>
            r.OperationId == operationId && r.Action == RentalItem.ApplyReturnedUnitsAction &&
            r.TargetItemId == item.Id);

        if (receipt is null || string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            return Error.Validation("RETURN_COMMAND_NOT_FOUND", "Matching return command was not found.");
        }

        ApplyReturnedUnitsCommandPayload? command;
        try { command = JsonSerializer.Deserialize<ApplyReturnedUnitsCommandPayload>(receipt.PayloadJson, JsonOptions); }
        catch (JsonException) { return Error.Validation("INVALID_RETURN_COMMAND", "Stored return command is invalid."); }

        if (command is null || command.OperationKey != operationKey ||
            command.RentalOrderId != order.Id || command.RentalItemId != item.Id ||
            command.ReturnRecordId != returnRecordId || command.AllocationGroupId != allocationGroupId ||
            command.Units is null)
        {
            return Error.Validation("RETURN_RESULT_MISMATCH", "Inventory result differs from the command.");
        }

        return (order, item, receipt, command);
    }

    private static bool IsProtected(string status) => status is
        "MAINTENANCE" or "OUT_OF_SERVICE" or "BLOCKED" or "CALENDAR_BLOCKED";
}
