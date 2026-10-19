using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.Policies;

namespace Dprental.RentalService.Application.Kafka.Consumers;

/// <summary>
/// Applies Inventory's confirmed return result; publishing the command alone never closes an assignment.
/// Enforces terminal state invariants: once Succeeded or Failed, conflicting events are rejected and do not mutate data.
/// </summary>
public sealed class InventoryReturnResultHandler(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result> ApplyAsync(string json, CancellationToken ct = default)
    {
        KafkaMessageEnvelope<ReturnedUnitsAppliedPayload>? envelope;
        try { envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<ReturnedUnitsAppliedPayload>>(json, JsonOptions); }
        catch (JsonException) { return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result is not valid JSON."); }

        if (envelope is null || envelope.MessageId == Guid.Empty ||
            envelope.Version != KafkaContractSpec.CurrentEnvelopeVersion ||
            envelope.MessageType != KafkaContractSpec.EventTypes.ReturnedUnitsApplied ||
            !string.Equals(envelope.Producer, KafkaContractSpec.Producers.InventoryService, StringComparison.OrdinalIgnoreCase) ||
            envelope.Payload is null || envelope.Payload.RentalOrderId == Guid.Empty ||
            envelope.Payload.RentalItemId == Guid.Empty || envelope.Payload.ReturnRecordId == Guid.Empty ||
            envelope.AggregateId != envelope.Payload.RentalOrderId.ToString("D"))
        {
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result envelope is invalid.");
        }

        var result = envelope.Payload;
        if (string.IsNullOrWhiteSpace(result.ReceiptRef) ||
            string.IsNullOrWhiteSpace(result.AllocationGroupId) ||
            string.IsNullOrWhiteSpace(result.OperationKey) ||
            result.Units is null || result.Units.Count == 0 ||
            result.Units.Select(u => u.AllocationId).Distinct().Count() != result.Units.Count)
        {
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result payload is invalid.");
        }

        await using var orderLock = await locks.AcquireOrderLockAsync(result.RentalOrderId, TimeSpan.FromSeconds(5), ct);

        var targetResult = await ValidateTargetAsync(result.RentalOrderId, result.RentalItemId, result.AllocationGroupId, result.ReturnRecordId, result.OperationKey, ct);
        if (targetResult.IsFailure) return targetResult.Error;

        var (order, item, receipt, command) = targetResult.Value;

        // Terminal states check
        if (receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return !string.IsNullOrWhiteSpace(receipt.ApiResponseJson) &&
                   CanonicalJson.AreEquivalent(receipt.ApiResponseJson, JsonSerializer.Serialize(result, JsonOptions))
                ? Result.Success()
                : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Another Inventory result was already applied.");
        }

        if (receipt.Status == RentalOperationReceipt.StatusFailed)
        {
            return Error.Conflict("INVENTORY_RESULT_CONFLICT", "Return operation was already marked as failed; cannot apply late result.");
        }

        // Validate units payload against command
        if (command.Units.Count != result.Units.Count)
        {
            return Error.Validation("RETURN_RESULT_MISMATCH", "Inventory result units count differs from the command.");
        }

        foreach (var returned in result.Units)
        {
            var requested = command.Units.SingleOrDefault(u => u.AllocationId == returned.AllocationId);
            var assignment = item.UnitAssignments.SingleOrDefault(a => a.AllocationId == returned.AllocationId);
            if (requested is null || assignment is null || assignment.AssetId != returned.AssetId ||
                assignment.ReturnRecordId != result.ReturnRecordId || returned.AllocationClosedAt == default ||
                (requested.Condition == "DAMAGED" &&
                 (assignment.ReturnCondition != Common.Enums.UnitCondition.Damaged ||
                  string.IsNullOrWhiteSpace(returned.DamageDispositionRef) ||
                  !IsProtected(returned.Status))) ||
                (requested.Condition == "NORMAL" &&
                 (assignment.ReturnCondition != Common.Enums.UnitCondition.Normal ||
                  returned.DamageDispositionRef is not null ||
                  returned.Status is not ("RELEASED" or "CLOSED"))) ||
                requested.Condition is not ("NORMAL" or "DAMAGED"))
            {
                return Error.Validation("INVALID_INVENTORY_RESULT", "Inventory result has an invalid unit.");
            }
        }

        // Apply closures and damage blocks
        var utcNow = clock.GetUtcNow();
        foreach (var returned in result.Units)
        {
            var assignment = item.UnitAssignments.Single(a => a.AllocationId == returned.AllocationId);
            if (assignment.ReturnCondition == Common.Enums.UnitCondition.Damaged)
            {
                var blocked = item.ConfirmDamagedUnitBlocked(assignment.Id, returned.DamageDispositionRef!);
                if (blocked.IsFailure) return blocked.Error;
            }

            var closed = item.ConfirmReturnedUnitClosure(assignment.Id, returned.AllocationClosedAt,
                returned.DamageDispositionRef, result.ReceiptRef, utcNow);
            if (closed.IsFailure) return closed.Error;
        }

        receipt.MarkSucceeded(utcNow, null, JsonSerializer.Serialize(result, JsonOptions));
        RentalOrderStatusPolicy.Apply(order, utcNow, result.ReceiptRef ?? item.Id.ToString("D"));
        order.RecordReadModelUpdate(utcNow);
        await orders.UpdateAsync(order, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> RejectAsync(string json, CancellationToken ct = default)
    {
        KafkaMessageEnvelope<ReturnedUnitsRejectedPayload>? envelope;
        try { envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<ReturnedUnitsRejectedPayload>>(json, JsonOptions); }
        catch (JsonException) { return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result is not valid JSON."); }

        if (envelope is null || envelope.MessageId == Guid.Empty ||
            envelope.Version != KafkaContractSpec.CurrentEnvelopeVersion ||
            envelope.MessageType != KafkaContractSpec.EventTypes.ReturnedUnitsRejected ||
            !string.Equals(envelope.Producer, KafkaContractSpec.Producers.InventoryService, StringComparison.OrdinalIgnoreCase) ||
            envelope.Payload is null || envelope.Payload.RentalOrderId == Guid.Empty ||
            envelope.Payload.RentalItemId == Guid.Empty || envelope.Payload.ReturnRecordId == Guid.Empty ||
            envelope.AggregateId != envelope.Payload.RentalOrderId.ToString("D"))
        {
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result envelope is invalid.");
        }

        var result = envelope.Payload;
        await using var orderLock = await locks.AcquireOrderLockAsync(result.RentalOrderId, TimeSpan.FromSeconds(5), ct);

        var targetResult = await ValidateTargetAsync(result.RentalOrderId, result.RentalItemId, result.AllocationGroupId, result.ReturnRecordId, result.OperationKey, ct);
        if (targetResult.IsFailure) return targetResult.Error;

        var (order, item, receipt, command) = targetResult.Value;

        // Terminal states check
        if (receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return Error.Conflict("INVENTORY_RESULT_CONFLICT", "Return operation already succeeded; rejection cannot override it.");
        }

        if (receipt.Status == RentalOperationReceipt.StatusFailed)
        {
            // Replay matching rejection is no-op
            return string.Equals(receipt.ErrorCode, result.ErrorCode, StringComparison.OrdinalIgnoreCase)
                ? Result.Success()
                : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Return operation already failed with different error code.");
        }

        var utcNow = clock.GetUtcNow();
        receipt.MarkFailed(result.ErrorCode, result.ErrorMessage, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        await orders.UpdateAsync(order, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

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
