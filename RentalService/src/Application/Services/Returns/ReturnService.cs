using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.DTOs.Integration;
using Microsoft.Extensions.Configuration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Kafka.Producers;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

public sealed partial class ReturnService(
    IRentalOrderRepository repository,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalMediaClient mediaClient,
    TimeProvider timeProvider,
    IRentalEventProducer rentalEvents,
    IConfiguration configuration,
    IInventoryCommandProducer inventoryCommands,
    IOrderHubNotifier? notifier = null,
    ILogger<ReturnService>? logger = null) : IReturnService
{

    public async Task<Result<RecordReturnResponse>> RecordReturnAsync(Guid itemId, IRentalCallerContext caller,
        RecordReturnRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        if (supplier.IsFailure) return supplier.Error;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");
        if (request?.Units is null || request.Units.Count == 0 || request.Units.Any(u => u is null || u.MediaIds is null))
            return Error.Validation("Return.InvalidUnits", "Return units and media collections are required.");
        bool kafkaEnabled = configuration.GetValue("Kafka:Enabled", true);
        if (!kafkaEnabled)
        {
            return Error.ServiceUnavailable("KAFKA_UNAVAILABLE", "Kafka service is disabled or unavailable.");
        }
        Guid supplierId = supplier.Value;
        var orderId = await repository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await repository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null) return RentalErrors.ItemNotFound;
        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;
        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            SupplierId = supplierId,
            ActualReturnAt = request.ActualReturnAt.ToUniversalTime(),
            request.Note,
            Units = request.Units.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                u.Condition,
                u.Description,
                MediaIds = u.MediaIds.OrderBy(id => id)
            })
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (receipt is not null)
        {
            if (receipt.Action != "RecordReturn" || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            if (!receipt.IsSuccess || string.IsNullOrWhiteSpace(receipt.ApiResponseJson))
                return Error.Conflict("RETURN_IN_PROGRESS", "The return operation is not complete.");
            var saved = JsonSerializer.Deserialize<RecordReturnResponse>(receipt.ApiResponseJson);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }
        var coreResult = await ExecuteReturnConfirmationCoreAsync(
            order,
            item,
            request.ActualReturnAt,
            request.Note,
            request.Units,
            supplierId,
            ActorRole.Supplier,
            key,
            fingerprint,
            payload,
            "RecordReturn",
            "RecordReturn",
            "Supplier recorded returned units",
            null,
            (rec, processedAt) => JsonSerializer.Serialize(new RecordReturnResponse(
                rec.Id,
                processedAt,
                item.UnitAssignments.Count(a => !a.ReturnRecordId.HasValue),
                item.Status,
                item.DeductionDeadline)),
            [OrderChangeTypes.ReturnSubmitted],
            cancellationToken);

        if (coreResult.IsFailure) return coreResult.Error;
        var (record, now) = coreResult.Value;
        return new RecordReturnResponse(
            record.Id,
            now,
            item.UnitAssignments.Count(a => !a.ReturnRecordId.HasValue),
            item.Status,
            item.DeductionDeadline);
    }

    private async Task<Result<(ReturnRecord Record, DateTimeOffset ProcessedAt)>> ExecuteReturnConfirmationCoreAsync(
        RentalOrder order,
        RentalItem item,
        DateTimeOffset actualReturnAt,
        string? note,
        List<ReturnUnitRequest> units,
        Guid actorId,
        ActorRole actorRole,
        string idempotencyKey,
        string fingerprint,
        string payloadJson,
        string operationAction,
        string transitionAction,
        string transitionNote,
        Action<ReturnRecord, DateTimeOffset>? onBeforeCommit,
        Func<ReturnRecord, DateTimeOffset, string> buildApiResponseJson,
        IReadOnlyList<string> notificationChangeTypes,
        CancellationToken cancellationToken)
    {
        bool kafkaEnabled = configuration.GetValue("Kafka:Enabled", true);
        if (!kafkaEnabled)
        {
            return Error.ServiceUnavailable("KAFKA_UNAVAILABLE", "Kafka service is disabled or unavailable.");
        }

        if (item.Status != RentalItemStatus.InRental)
            return RentalErrors.InvalidItemStatusForReturn;

        if (actualReturnAt == default || note?.Length > 2000)
            return Error.Validation("Return.InvalidRequest", "Invalid return time or note.");

        var now = timeProvider.GetUtcNow();
        if (actualReturnAt > now)
            return RentalErrors.FutureReturnTimeNotAllowed;

        if (units.Select(u => u.RentalUnitAssignmentId).Distinct().Count() != units.Count)
            return Error.Validation("Return.InvalidUnits", "Specify distinct assignments to return.");

        foreach (var unit in units)
        {
            var assignment = item.UnitAssignments.FirstOrDefault(a => a.Id == unit.RentalUnitAssignmentId);
            if (assignment is null)
                return RentalErrors.AssignmentNotFound;

            var eligibility = ReturnEligibilityPolicy.ValidateAssignmentEligibleForReturn(item, assignment, actualReturnAt);
            if (eligibility.IsFailure)
                return eligibility.Error;

            if (!Enum.IsDefined(unit.Condition))
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

            if (unit.Description?.Length > 2000)
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

            if (unit.MediaIds.Count > 10 || unit.MediaIds.Contains(Guid.Empty) ||
                unit.MediaIds.Distinct().Count() != unit.MediaIds.Count)
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

            if (unit.Condition == UnitCondition.Damaged)
            {
                if (string.IsNullOrWhiteSpace(unit.Description))
                    return Error.Validation(actorRole == ActorRole.Admin ? "Return.DamagedDescriptionRequired" : "Return.InvalidEvidence",
                        actorRole == ActorRole.Admin ? "Description is required when unit condition is Damaged." : "Damaged unit requires a description.");

                if (unit.MediaIds.Count == 0)
                    return Error.Validation(actorRole == ActorRole.Admin ? "Return.DamagedMediaRequired" : "Return.InvalidEvidence",
                        actorRole == ActorRole.Admin ? "Media evidence is required when unit condition is Damaged." : "Damaged unit requires at least one media evidence.");
            }
        }

        foreach (var unit in units)
        {
            if (unit.MediaIds.Count == 0 && unit.Condition != UnitCondition.Damaged) continue;
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                unit.MediaIds, actorId, mediaClient, isRequired: unit.Condition == UnitCondition.Damaged, ct: cancellationToken);
            if (evidence.IsFailure) return evidence.Error;
        }

        now = timeProvider.GetUtcNow();
        var previousStatus = item.Status;

        var record = new ReturnRecord(
            Guid.NewGuid(),
            item.Id,
            actualReturnAt.ToUniversalTime(),
            note,
            actorId,
            now.ToUniversalTime());
        item.AddReturnRecord(record);

        foreach (var unit in units)
        {
            var assignment = item.UnitAssignments.First(a => a.Id == unit.RentalUnitAssignmentId);
            assignment.RecordReturn(record.Id, unit.Condition, unit.Description, unit.MediaIds, null, null);
            record.AddAssignment(assignment);
        }

        var returnedAssignmentIds = units.Select(u => u.RentalUnitAssignmentId).ToHashSet();
        foreach (var reqUnit in item.ReturnRequests.SelectMany(r => r.Units))
        {
            if (returnedAssignmentIds.Contains(reqUnit.RentalUnitAssignmentId) && reqUnit.IsActionable)
            {
                reqUnit.SetClosedAt(now);
            }
        }

        if (item.AreAllUnitsReturned)
        {
            var completedAt = now.ToUniversalTime();
            if (item.HasDamagedUnits && item.DepositAmount > 0m)
            {
                item.SetReturnsCompleted(completedAt, completedAt.AddHours(48), null);
            }
            else
            {
                item.SetReturnsCompleted(completedAt, null, 0m);
            }
        }

        onBeforeCommit?.Invoke(record, now);

        RentalOrderStatusPolicy.Apply(order, now, idempotencyKey);
        order.RecordReadModelUpdate(now);

        order.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(), order.Id, item.Id,
            previousStatus.ToString(), item.Status.ToString(),
            transitionAction, actorId, actorRole,
            transitionNote, now, idempotencyKey));

        string apiResponseJson = buildApiResponseJson(record, now);

        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, idempotencyKey, operationAction,
            fingerprint, true, null, null, now, targetItemId: item.Id,
            payloadJson: payloadJson, apiResponseJson: apiResponseJson));

        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var returnCmd = ScheduleKafkaInventoryReturn(order, item, record, now);
            var enqueueResult = await inventoryCommands.EnqueueApplyReturnedUnitsAsync(returnCmd, cancellationToken);
            if (enqueueResult.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }

            var returnedEvent = await rentalEvents.EnqueueRentalUnitsReturnedAsync(
                new RentalUnitsReturnedPayload(order.Id, item.Id, record.Id,
                    actualReturnAt.ToUniversalTime(), now,
                    units.Select(u => u.RentalUnitAssignmentId).ToArray(),
                    order.Version),
                correlationId: idempotencyKey, cancellationToken: cancellationToken);
            if (returnedEvent.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                unitOfWork.DiscardChanges();
                return returnedEvent.Error;
            }

            await repository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }

        if (notifier != null)
        {
            foreach (var changeType in notificationChangeTypes)
            {
                await NotifyParticipantsOrderChangedAsync(order, item, changeType, now, cancellationToken);
            }
        }

        return (record, now);
    }
    internal static ApplyReturnedUnitsCommandPayload ScheduleKafkaInventoryReturn(
        RentalOrder order, RentalItem item, ReturnRecord record, DateTimeOffset now)
    {
        var units = item.UnitAssignments
            .Where(a => a.ReturnRecordId == record.Id)
            .OrderBy(a => a.AllocationId)
            .Select(a => new ApplyReturnedUnitPayload(a.AllocationId,
                a.ReturnCondition == UnitCondition.Damaged ? "DAMAGED" : "NORMAL"))
            .ToArray();
        string operationId = $"return:{record.Id:N}:apply";
        string operationKey = RentalOperationExecutor.ComputeOperationKey(
            RentalItem.ApplyReturnedUnitsAction, operationId);
        var command = new ApplyReturnedUnitsCommandPayload(order.Id, item.Id,
            item.AllocationGroupId, record.Id, record.ActualReturnAt, operationKey, units);
        var json = JsonSerializer.Serialize(command);
        order.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(),
            order.Id, operationId, RentalItem.ApplyReturnedUnitsAction,
            RentalOperationExecutor.ComputeFingerprint(json), now, item.Id, json));
        return command;
    }


}
