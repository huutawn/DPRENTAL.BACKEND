using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Kafka.Consumers;

/// <summary>Applies Inventory's confirmed result; publishing the command alone never closes an assignment.</summary>
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
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result envelope is invalid.");

        var result = envelope.Payload;
        if (string.IsNullOrWhiteSpace(result.ReceiptRef) ||
            string.IsNullOrWhiteSpace(result.AllocationGroupId) ||
            string.IsNullOrWhiteSpace(result.OperationKey) ||
            result.Units is null || result.Units.Count == 0 ||
            result.Units.Select(u => u.AllocationId).Distinct().Count() != result.Units.Count)
            return Error.Validation("INVALID_INVENTORY_EVENT", "Inventory result payload is invalid.");

        await using var orderLock = await locks.AcquireOrderLockAsync(
            result.RentalOrderId, TimeSpan.FromSeconds(5), ct);
        var order = await orders.GetByIdWithDetailsAsync(result.RentalOrderId, ct);
        if (order is null) return Error.NotFound("RETURN_ORDER_NOT_FOUND", "Return order was not found.");
        var item = order.Items.FirstOrDefault(i => i.Id == result.RentalItemId);
        if (item is null || item.AllocationGroupId != result.AllocationGroupId ||
            !item.ReturnRecords.Any(r => r.Id == result.ReturnRecordId))
            return Error.Validation("INVALID_RETURN_TARGET", "Inventory result does not match the return.");

        string operationId = $"return:{result.ReturnRecordId:N}:apply";
        var receipt = order.OperationReceipts.FirstOrDefault(r =>
            r.OperationId == operationId && r.Action == RentalItem.ApplyReturnedUnitsAction &&
            r.TargetItemId == item.Id);
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.PayloadJson))
            return Error.Validation("RETURN_COMMAND_NOT_FOUND", "Matching return command was not found.");
        ApplyReturnedUnitsCommandPayload? command;
        try { command = JsonSerializer.Deserialize<ApplyReturnedUnitsCommandPayload>(receipt.PayloadJson, JsonOptions); }
        catch (JsonException) { return Error.Validation("INVALID_RETURN_COMMAND", "Stored return command is invalid."); }
        if (command is null || command.OperationKey != result.OperationKey ||
            command.RentalOrderId != order.Id || command.RentalItemId != item.Id ||
            command.ReturnRecordId != result.ReturnRecordId || command.AllocationGroupId != result.AllocationGroupId ||
            command.Units is null || command.Units.Count != result.Units.Count)
            return Error.Validation("RETURN_RESULT_MISMATCH", "Inventory result differs from the command.");

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
                return Error.Validation("INVALID_INVENTORY_RESULT", "Inventory result has an invalid unit.");
        }

        if (receipt.IsSuccess)
        {
            return receipt.ResponseJson is not null && CanonicalJson.AreEquivalent(
                receipt.ResponseJson, JsonSerializer.Serialize(result, JsonOptions))
                ? Result.Success()
                : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Another Inventory result was already applied.");
        }

        foreach (var returned in result.Units)
        {
            var assignment = item.UnitAssignments.Single(a => a.AllocationId == returned.AllocationId);
            if (assignment.ReturnCondition == Common.Enums.UnitCondition.Damaged)
            {
                var blocked = item.ConfirmDamagedUnitBlocked(assignment.Id, returned.DamageDispositionRef!);
                if (blocked.IsFailure) return blocked.Error;
            }
            var closed = item.ConfirmReturnedUnitClosure(assignment.Id, returned.AllocationClosedAt,
                returned.DamageDispositionRef, result.ReceiptRef, clock.GetUtcNow());
            if (closed.IsFailure) return closed.Error;
        }
        receipt.MarkSucceeded(clock.GetUtcNow(), JsonSerializer.Serialize(result, JsonOptions));
        order.IncrementVersion();
        await orders.UpdateAsync(order, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static bool IsProtected(string status) => status is
        "MAINTENANCE" or "OUT_OF_SERVICE" or "BLOCKED" or "CALENDAR_BLOCKED";
}
