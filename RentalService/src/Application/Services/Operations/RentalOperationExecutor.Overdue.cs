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

        if (_inventoryClient != null)
        {
            await _inventoryClient.MarkAssetsOverdueAsync(item.AllocationGroupId, command.ProviderRequest, cancellationToken);
        }

        receipt.MarkSucceeded(now);
        await CommitLocalChangesAsync(order, cancellationToken);
        return Result.Success();
    }
}
