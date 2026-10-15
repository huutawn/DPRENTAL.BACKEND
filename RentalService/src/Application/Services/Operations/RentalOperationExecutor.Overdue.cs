using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    internal async Task<Result> MarkAssetsOverdueCoreAsync(RentalOrder order, RentalItem item,
        RentalOperationReceipt receipt, CancellationToken cancellationToken)
    {
        OverdueProtectionCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<OverdueProtectionCommand>(receipt.PayloadJson ?? "", JsonOptions);
        }
        catch (JsonException)
        {
            return Error.Unprocessable("CORRUPTED_OVERDUE_COMMAND", "Payload bảo vệ quá hạn không hợp lệ.");
        }
        if (command is null || command.AssignmentId == Guid.Empty || command.ProviderRequest is null ||
            command.ProviderRequest.AllocationIds is not { Count: 1 } ||
            string.IsNullOrWhiteSpace(command.ProviderRequest.OperationKey) ||
            command.ProviderRequest.OperationKey != ComputeOperationKey(
                RentalItem.OverdueProtectionAction, command.AssignmentId.ToString("D")))
            return Error.Unprocessable("CORRUPTED_OVERDUE_COMMAND", "Payload bảo vệ quá hạn không hợp lệ.");

        RentalUnitAssignment? unit = item.UnitAssignments.FirstOrDefault(u => u.Id == command.AssignmentId);
        if (unit is null || unit.AllocationId != command.ProviderRequest.AllocationIds[0])
            return Error.Unprocessable("INVALID_OVERDUE_TARGET", "Allocation không thuộc mục thuê.");

        DateTimeOffset now = _timeProvider.GetUtcNow();
        // The order lock serializes this recheck with supplier return recording.
        if (!item.IsOverdue(now) || unit.Status != RentalUnitAssignmentStatus.HandedOver ||
            unit.ReturnRecordId.HasValue)
        {
            receipt.MarkSucceeded(now);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }
        receipt.BeginProviderAttempt(now);
        await CommitLocalChangesAsync(order, cancellationToken);

        var result = await _inventoryClient.MarkAssetsOverdueAsync(
            item.AllocationGroupId, command.ProviderRequest, cancellationToken);
        now = _timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            var response = result.Value;
            if (string.IsNullOrWhiteSpace(response.ReceiptRef) ||
                !string.Equals(response.OperationKey, command.ProviderRequest.OperationKey, StringComparison.Ordinal) ||
                !string.Equals(response.AllocationGroupId, item.AllocationGroupId, StringComparison.Ordinal) ||
                response.Units is not { Count: 1 } ||
                response.Units[0].AllocationId != unit.AllocationId ||
                response.Units[0].AssetId != unit.AssetId ||
                !string.Equals(response.Units[0].Status, "PROTECTED", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(response.Units[0].OverdueProtectionRef))
            {
                var invalid = Error.ServiceUnavailable("INVALID_OVERDUE_RESPONSE", "Inventory trả thông tin bảo vệ quá hạn không hợp lệ.");
                RecordExecutionFailure(receipt, invalid, now, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return invalid;
            }
            receipt.MarkSucceeded(now, JsonSerializer.Serialize(response, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        RecordExecutionFailure(receipt, result.Error, now);
        await CommitLocalChangesAsync(order, cancellationToken);
        return result.Error;
    }
}
