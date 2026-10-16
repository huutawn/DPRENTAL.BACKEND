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
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.DTOs.Integration;
using Microsoft.Extensions.Configuration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Kafka.Producers;

namespace Dprental.RentalService.Application.Services.Returns;

public sealed class ReturnService(
    IRentalOrderRepository repository,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalMediaClient mediaClient,
    TimeProvider timeProvider,
    IOutboxRepository outbox,
    IConfiguration configuration,
    IInventoryCommandProducer inventoryCommands) : IReturnService
{

    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);
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
        var domainUnits = request.Units.Select(u => new ReturnUnitData(
            u.RentalUnitAssignmentId, u.Condition, u.Description, u.MediaIds)).ToList();
        var validation = item.ValidateAssetReturns(supplierId, request.ActualReturnAt, request.Note,
            domainUnits, timeProvider.GetUtcNow(), supplierId);
        if (validation.IsFailure) return validation.Error;
        foreach (var unit in request.Units)
        {
            if (unit.MediaIds.Count == 0) continue;
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                unit.MediaIds, supplierId, mediaClient, ct: cancellationToken);
            if (evidence.IsFailure) return evidence.Error;
        }
        var now = timeProvider.GetUtcNow();
        var previousStatus = item.Status;
        var recorded = item.RecordAssetReturns(supplierId, request.ActualReturnAt, request.Note,
            domainUnits,
            now, supplierId);
        if (recorded.IsFailure) return recorded.Error;
        var response = new RecordReturnResponse(recorded.Value.Id, now,
            item.UnitAssignments.Count(a => !a.ReturnRecordId.HasValue), item.Status, item.DeductionDeadline);
        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), order.Id, item.Id,
            previousStatus.ToString(), item.Status.ToString(), "RecordReturn", supplierId, ActorRole.Supplier,
            "Supplier recorded returned units", now, key));
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key, "RecordReturn",
            fingerprint, true, null, null, now, targetItemId: item.Id, payloadJson: payload,
            apiResponseJson: JsonSerializer.Serialize(response)));
        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var returnCmd = ScheduleKafkaInventoryReturn(order, item, recorded.Value, now);
            var enqueueResult = await inventoryCommands.EnqueueApplyReturnedUnitsAsync(returnCmd, cancellationToken);
            if (enqueueResult.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }
            order.IncrementVersion();

            var envelope = new KafkaMessageEnvelope<RentalUnitsReturnedPayload>(Guid.NewGuid(), "RentalUnitsReturned",
                1, "rental-service", order.Id.ToString("D"), now, key, null,
                new RentalUnitsReturnedPayload(order.Id, item.Id, recorded.Value.Id,
                    request.ActualReturnAt.ToUniversalTime(), now,
                    request.Units.Select(u => u.RentalUnitAssignmentId).ToArray()));
            await outbox.AddAsync(new RentalOutboxMessage(envelope.MessageId,
                configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1", order.Id.ToString("D"),
                envelope.MessageType, JsonSerializer.Serialize(envelope, EventJsonOptions), now), cancellationToken);
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
        return response;
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
