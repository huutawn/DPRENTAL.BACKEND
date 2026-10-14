using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    internal async Task<Result> BlockDamagedUnitsCoreAsync(RentalOrder order, RentalItem item,
        RentalOperationReceipt receipt, CancellationToken ct)
    {
        BlockDamagedAssetsRequest? request;
        try { request = JsonSerializer.Deserialize<BlockDamagedAssetsRequest>(receipt.PayloadJson ?? "", JsonOptions); }
        catch (JsonException) { return Error.Unprocessable("CORRUPTED_DAMAGE_COMMAND", "Damage command is invalid."); }
        if (request is null || request.ReturnRecordId == Guid.Empty ||
            request.AllocationIds is null || request.AllocationIds.Count == 0 ||
            request.AllocationIds.Distinct().Count() != request.AllocationIds.Count ||
            request.OperationKey != ComputeOperationKey(RentalItem.BlockDamagedAction, receipt.OperationId))
            return Error.Unprocessable("CORRUPTED_DAMAGE_COMMAND", "Damage command is invalid.");

        var units = item.UnitAssignments.Where(a => request.AllocationIds.Contains(a.AllocationId)).ToArray();
        if (units.Length != request.AllocationIds.Count || units.Any(a =>
            a.ReturnRecordId != request.ReturnRecordId || a.Status != RentalUnitAssignmentStatus.ReturnedDamaged))
            return Error.Unprocessable("INVALID_DAMAGE_TARGET", "Damage command does not match returned units.");
        if (!item.ReturnRecords.Any(r => r.Id == request.ReturnRecordId))
            return Error.Unprocessable("INVALID_DAMAGE_TARGET", "Return record was not found.");

        receipt.BeginProviderAttempt(_timeProvider.GetUtcNow());
        await CommitLocalChangesAsync(order, ct);
        var result = await _inventoryClient.BlockDamagedAssetsAsync(item.AllocationGroupId, request, ct);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (result.IsFailure)
        {
            RecordExecutionFailure(receipt, result.Error, now);
            await CommitLocalChangesAsync(order, ct);
            return result.Error;
        }

        var response = result.Value;
        if (string.IsNullOrWhiteSpace(response.ReceiptRef) ||
            response.OperationKey != request.OperationKey || response.AllocationGroupId != item.AllocationGroupId ||
            response.Units is null || response.Units.Count != units.Length ||
            response.Units.Select(u => u.AllocationId).Distinct().Count() != units.Length ||
            response.Units.Any(u => !units.Any(a => a.AllocationId == u.AllocationId &&
                a.AssetId == u.AssetId) || string.IsNullOrWhiteSpace(u.DamageDispositionRef) ||
                !IsProtectedDamageStatus(u.Status) ||
                units.Single(a => a.AllocationId == u.AllocationId).InventoryDispositionRef is { } existing &&
                existing != u.DamageDispositionRef))
            return await InvalidReturnResponseAsync(order, receipt, "INVALID_DAMAGE_RESPONSE", now, ct);

        foreach (var returned in response.Units)
        {
            var unit = units.Single(a => a.AllocationId == returned.AllocationId);
            var recorded = item.ConfirmDamagedUnitBlocked(unit.Id, returned.DamageDispositionRef);
            if (recorded.IsFailure)
                return await InvalidReturnResponseAsync(order, receipt, recorded.Error.Code, now, ct);
        }
        receipt.MarkSucceeded(now, JsonSerializer.Serialize(response, JsonOptions));
        await CommitLocalChangesAsync(order, ct);
        return Result.Success();
    }

    internal async Task<Result> MarkReturnedUnitsCoreAsync(RentalOrder order, RentalItem item,
        RentalOperationReceipt receipt, CancellationToken ct)
    {
        MarkAssetsReturnedRequest? request;
        try { request = JsonSerializer.Deserialize<MarkAssetsReturnedRequest>(receipt.PayloadJson ?? "", JsonOptions); }
        catch (JsonException) { return Error.Unprocessable("CORRUPTED_RETURN_COMMAND", "Return command is invalid."); }
        if (request is null || request.ReturnRecordId == Guid.Empty ||
            request.Units is null || request.Units.Count == 0 ||
            request.Units.Select(u => u.AllocationId).Distinct().Count() != request.Units.Count ||
            request.OperationKey != ComputeOperationKey(RentalItem.MarkReturnedAction, receipt.OperationId))
            return Error.Unprocessable("CORRUPTED_RETURN_COMMAND", "Return command is invalid.");

        var returnRecord = item.ReturnRecords.FirstOrDefault(r => r.Id == request.ReturnRecordId);
        if (returnRecord is null || returnRecord.ActualReturnAt != request.ActualReturnAt.ToUniversalTime())
            return Error.Unprocessable("INVALID_RETURN_TARGET", "Return record does not match the command.");

        var units = item.UnitAssignments.Where(a => request.Units.Any(u => u.AllocationId == a.AllocationId)).ToArray();
        if (units.Length != request.Units.Count || units.Any(a =>
            a.ReturnRecordId != request.ReturnRecordId ||
            request.Units.Single(u => u.AllocationId == a.AllocationId).Condition != a.ReturnCondition ||
            a.Status is not (RentalUnitAssignmentStatus.ReturnedNormal or RentalUnitAssignmentStatus.ReturnedDamaged) ||
            (a.Status == RentalUnitAssignmentStatus.ReturnedDamaged && string.IsNullOrWhiteSpace(a.InventoryDispositionRef))))
            return Error.Unprocessable("INVALID_RETURN_TARGET", "Return command does not match protected returned units.");

        receipt.BeginProviderAttempt(_timeProvider.GetUtcNow());
        await CommitLocalChangesAsync(order, ct);
        var result = await _inventoryClient.MarkAssetsReturnedAsync(item.AllocationGroupId, request, ct);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (result.IsFailure)
        {
            RecordExecutionFailure(receipt, result.Error, now);
            await CommitLocalChangesAsync(order, ct);
            return result.Error;
        }

        var response = result.Value;
        if (string.IsNullOrWhiteSpace(response.ReceiptRef) ||
            response.OperationKey != request.OperationKey || response.AllocationGroupId != item.AllocationGroupId ||
            response.Units is null || response.Units.Count != units.Length ||
            response.Units.Select(u => u.AllocationId).Distinct().Count() != units.Length ||
            response.Units.Any(u => !units.Any(a => a.AllocationId == u.AllocationId && a.AssetId == u.AssetId) ||
                !u.AllocationClosedAt.HasValue ||
                (units.Single(a => a.AllocationId == u.AllocationId).Status == RentalUnitAssignmentStatus.ReturnedDamaged &&
                 u.DamageDispositionRef != units.Single(a => a.AllocationId == u.AllocationId).InventoryDispositionRef) ||
                (units.Single(a => a.AllocationId == u.AllocationId).AllocationClosedAt is { } closedAt &&
                 closedAt != u.AllocationClosedAt)))
            return await InvalidReturnResponseAsync(order, receipt, "INVALID_RETURN_RESPONSE", now, ct);

        foreach (var returned in response.Units)
        {
            var unit = units.Single(a => a.AllocationId == returned.AllocationId);
            var recorded = item.ConfirmReturnedUnitClosure(unit.Id, returned.AllocationClosedAt!.Value,
                returned.DamageDispositionRef, response.ReceiptRef, now);
            if (recorded.IsFailure)
                return await InvalidReturnResponseAsync(order, receipt, recorded.Error.Code, now, ct);
        }
        receipt.MarkSucceeded(now, JsonSerializer.Serialize(response, JsonOptions));
        await CommitLocalChangesAsync(order, ct);
        return Result.Success();
    }

    private async Task<Result> InvalidReturnResponseAsync(RentalOrder order, RentalOperationReceipt receipt,
        string code, DateTimeOffset now, CancellationToken ct)
    {
        var error = Error.ServiceUnavailable(code, "Inventory returned an inconsistent result.");
        RecordExecutionFailure(receipt, error, now, forceUncertain: true);
        await CommitLocalChangesAsync(order, ct);
        return error;
    }

    private static bool IsProtectedDamageStatus(string? status) =>
        status is not null && (status.Equals("MAINTENANCE", StringComparison.OrdinalIgnoreCase) ||
                               status.Equals("OUT_OF_SERVICE", StringComparison.OrdinalIgnoreCase) ||
                               status.Equals("BLOCKED", StringComparison.OrdinalIgnoreCase) ||
                               status.Equals("CALENDAR_BLOCKED", StringComparison.OrdinalIgnoreCase));
}
