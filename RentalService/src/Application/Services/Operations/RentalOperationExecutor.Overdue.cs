using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.Kafka.Contracts;
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
        try { command = JsonSerializer.Deserialize<OverdueProtectionCommand>(receipt.PayloadJson ?? "", JsonOptions); }
        catch (JsonException) { return Error.Unprocessable("CORRUPTED_OVERDUE_COMMAND", "Payload bảo vệ quá hạn không hợp lệ."); }

        if (command is null || command.AssignmentId == Guid.Empty || command.ProviderRequest is null ||
            command.ProviderRequest.AllocationIds is not { Count: 1 } || string.IsNullOrWhiteSpace(command.ProviderRequest.OperationKey) ||
            command.ProviderRequest.OperationKey != ComputeOperationKey(RentalItem.OverdueProtectionAction, command.AssignmentId.ToString("D")))
            return Error.Unprocessable("CORRUPTED_OVERDUE_COMMAND", "Payload bảo vệ quá hạn không hợp lệ.");

        RentalUnitAssignment? unit = item.UnitAssignments.FirstOrDefault(u => u.Id == command.AssignmentId);
        if (unit is null || unit.AllocationId != command.ProviderRequest.AllocationIds[0])
            return Error.Unprocessable("INVALID_OVERDUE_TARGET", "Allocation không thuộc mục thuê.");

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!item.IsOverdue(now) || unit.Status != RentalUnitAssignmentStatus.HandedOver || unit.ReturnRecordId.HasValue)
        {
            receipt.MarkSucceeded(now);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        var enqueue = await _inventoryCommands.EnqueueMarkAssetsOverdueAsync(
            new MarkAssetsOverdueCommandPayload(order.Id, item.Id, item.AllocationGroupId,
                command.ProviderRequest.OperationKey, command.ProviderRequest.AllocationIds),
            order.Id.ToString("D"), cancellationToken: cancellationToken);
        if (enqueue.IsFailure)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            return enqueue.Error;
        }
        receipt.MarkProcessing(now);
        await CommitLocalChangesAsync(order, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Result.Success();
    }
}
