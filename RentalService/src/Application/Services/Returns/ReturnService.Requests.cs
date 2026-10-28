using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
using Microsoft.Extensions.Logging;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Services.Returns;

public sealed partial class ReturnService
{
    private const string RequestReturnAction = "RequestReturn";
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<Result<ReturnRequestResponse>> CreateReturnRequestAsync(Guid itemId,
        IRentalCallerContext caller, CreateReturnRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");
        if (request?.Units is null || request.Units.Count == 0 ||
            request.Units.Any(u => u is null || u.RentalUnitAssignmentId == Guid.Empty || u.MediaIds is null) ||
            request.Units.Select(u => u.RentalUnitAssignmentId).Distinct().Count() != request.Units.Count)
            return Error.Validation("ReturnRequest.InvalidUnits", "Specify distinct assignments and media collections.");
        if (request.ActualReturnAt == default || request.Note?.Length > 2000)
            return Error.Validation("ReturnRequest.InvalidRequest", "Invalid return time or note.");

        var orderId = await repository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await repository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null) return RentalErrors.ItemNotFound;
        var authorization = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        if (authorization.IsFailure)
            return authorization.Error.Type == ErrorType.NotFound ? RentalErrors.ItemNotFound : authorization.Error;
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return RentalErrors.ItemNotFound;

        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            order.RenterId,
            ActualReturnAt = request.ActualReturnAt.ToUniversalTime(),
            request.Note,
            Units = request.Units.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                MediaIds = (u.MediaIds ?? []).OrderBy(id => id)
            })
        });
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(payload);
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (receipt is not null)
        {
            if (receipt.Action != RequestReturnAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(receipt.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<ReturnRequestResponse>(receipt.ApiResponseJson, ReceiptJsonOptions);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }

        var now = timeProvider.GetUtcNow();
        var validation = ValidateReturnRequest(item, request, now);
        if (validation.IsFailure) return validation.Error;
        foreach (var unit in request.Units!)
        {
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                unit.MediaIds, order.RenterId, mediaClient, ct: cancellationToken);
            if (evidence.IsFailure) return evidence.Error;
        }

        Guid requestId = Guid.NewGuid();
        var record = new ReturnRequest(requestId, itemId, request.ActualReturnAt.ToUniversalTime(),
            request.Note, order.RenterId, now, request.Units.Select(u =>
                new ReturnRequestUnit(Guid.NewGuid(), requestId, u.RentalUnitAssignmentId, u.MediaIds ?? [])));
        var response = new ReturnRequestResponse(record.Id, itemId, record.ActualReturnAt,
            record.Note, record.SubmittedAt, record.Units
                .OrderBy(u => u.RentalUnitAssignmentId)
                .Select(u => new ReturnRequestUnitResponse(u.RentalUnitAssignmentId, u.MediaIds, u.ClosedAt)).ToArray());
        item.AddReturnRequest(record);
        order.RecordReadModelUpdate(now);
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key, RequestReturnAction,
            fingerprint, true, null, null, now, targetItemId: itemId, payloadJson: payload,
            apiResponseJson: JsonSerializer.Serialize(response)));
        try
        {
            // SaveChanges commits request, units and receipt atomically, after media calls have finished.
            await repository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            unitOfWork.DiscardChanges();
            return Error.Conflict("RETURN_REQUEST_ALREADY_OPEN", "This unit already has an open return request.");
        }
        catch
        {
            unitOfWork.DiscardChanges();
            throw;
        }

        if (notifier != null)
        {
            try
            {
                await notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.ReturnRequested,
                        now),
                    cancellationToken);

                var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
                if (subOrder != null)
                {
                    await notifier.NotifySupplierSubOrderChangedAsync(
                        item.SupplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder.Id,
                            order.Version,
                            OrderChangeTypes.ReturnRequested,
                            now),
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger != null)
                {
                    LogReturnRequestedNotificationFailed(logger, ex, order.Id);
                }
            }
        }

        return response;
    }

    private static Result ValidateReturnRequest(RentalItem item, CreateReturnRequest request, DateTimeOffset now)
    {
        if (item.Status != RentalItemStatus.InRental) return RentalErrors.InvalidItemStatusForReturn;
        if (request.ActualReturnAt > now) return RentalErrors.FutureReturnTimeNotAllowed;
        var assignments = item.UnitAssignments.ToDictionary(u => u.Id);
        var openUnits = ReturnEligibilityPolicy.GetOpenReturnRequestAssignmentIds(item);
        foreach (var unit in request.Units!)
        {
            if (!assignments.TryGetValue(unit.RentalUnitAssignmentId, out var assignment))
                return RentalErrors.AssignmentNotFound;
            var validation = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(item, assignment, request.ActualReturnAt, openUnits);
            if (validation.IsFailure) return validation.Error;
        }
        return Result.Success();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send ReturnRequested notification for order {OrderId}")]
    private static partial void LogReturnRequestedNotificationFailed(ILogger logger, Exception ex, Guid orderId);
}
