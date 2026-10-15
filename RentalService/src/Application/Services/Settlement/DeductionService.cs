using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Settlement;

public sealed class DeductionService(
    IRentalOrderRepository orders, IOrderLockManager locks, IUnitOfWork unitOfWork,
    IRentalMediaClient media, TimeProvider clock) : IDeductionService
{
    public async Task<Result<SubmitDeductionResponse>> SubmitAsync(
        Guid itemId, IRentalCallerContext caller, SubmitDeductionRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (caller is null || !caller.IsAuthenticated || caller.IsService || caller.UserId is null || caller.UserId == Guid.Empty)
            return Error.Unauthorized("AUTH_REQUIRED", "Supplier authentication is required.");
        if (request is null || request.EvidenceMediaIds is null || request.AffectedAssetIds is null)
            return Error.Validation("INVALID_DEDUCTION_REQUEST", "Deduction request is incomplete.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");

        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (orderId is null) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        RentalOrder? order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        RentalItem? item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || order is null) return RentalErrors.ItemNotFound;
        Guid supplierId = caller.UserId.Value;
        if (item.SupplierId != supplierId) return RentalErrors.UnauthorizedSupplier;

        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId, SupplierId = supplierId, request.DeductionAmount,
            DamageDescription = request.DamageDescription?.Trim(),
            EvidenceMediaIds = request.EvidenceMediaIds.OrderBy(x => x),
            AffectedAssetIds = request.AffectedAssetIds.OrderBy(x => x)
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        RentalOperationReceipt? existing = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (existing is not null)
        {
            if (existing.Action != "SubmitDeduction" || existing.TargetItemId != itemId || existing.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(existing.ApiResponseJson)
                ? null : JsonSerializer.Deserialize<SubmitDeductionResponse>(existing.ApiResponseJson);
            return saved is null
                ? Error.Conflict("DEDUCTION_IN_PROGRESS", "Deduction submission is not complete.")
                : saved;
        }

        DateTimeOffset now = clock.GetUtcNow();
        var validation = item.ValidateDeductionSubmission(supplierId, request.DeductionAmount,
            request.DamageDescription, request.EvidenceMediaIds, request.AffectedAssetIds, now);
        if (validation.IsFailure) return validation.Error;
        var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
            request.EvidenceMediaIds, supplierId, media, isRequired: true, ct: cancellationToken);
        if (evidence.IsFailure) return evidence.Error;

        now = clock.GetUtcNow();
        var submitted = item.SubmitDeduction(supplierId, request.DeductionAmount,
            request.DamageDescription!, request.EvidenceMediaIds, request.AffectedAssetIds, now);
        if (submitted.IsFailure) return submitted.Error;
        var proposal = submitted.Value;
        var response = new SubmitDeductionResponse(proposal.Id, proposal.TotalProposedAmount,
            proposal.SubmittedAt, proposal.RenterResponseDeadline, item.Id, item.Status.ToString());
        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), order.Id, item.Id,
            RentalItemStatus.PendingSettlement.ToString(), RentalItemStatus.PendingSettlement.ToString(),
            "SubmitDeduction", supplierId, ActorRole.Supplier, "Supplier submitted damage deduction proposal",
            now, key));
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key,
            "SubmitDeduction", fingerprint, true, null, null, now, targetItemId: item.Id,
            payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));
        order.IncrementVersion();
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return response;
    }

    public async Task<int> ProcessDeductionTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await orders.GetDeductionTimeoutItemIdsAsync(clock.GetUtcNow(), 50, cancellationToken);
        int processed = 0;
        foreach (var id in ids)
        {
            if ((await ExpireSingleItemDeductionAsync(id, cancellationToken)).IsSuccess) processed++;
        }
        return processed;
    }

    public async Task<Result> ExpireSingleItemDeductionAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (orderId is null) return Result.Success();
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        RentalOrder? order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        RentalItem? item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || order is null) return Result.Success();
        DateTimeOffset now = clock.GetUtcNow();
        string operationId = $"deduction-timeout:{itemId:N}";
        if (!item.TryFinalizeZeroDeduction(now, operationId)) return Result.Success();
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id,
            operationId, "DeductionTimeout", operationId, true, null, null, now, targetItemId: itemId));
        order.IncrementVersion();
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<int> ScheduleReadySettlementsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await orders.GetSettlementReadyItemIdsAsync(50, cancellationToken);
        int count = 0;
        foreach (var id in ids)
        {
            var result = await ScheduleSettlementAsync(id, cancellationToken);
            if (result.IsSuccess) count++;
        }
        return count;
    }

    public async Task<Result> ScheduleSettlementAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (order is null || item is null) return RentalErrors.ItemNotFound;
        if (order.OperationReceipts.Any(r => r.Action == RentalItem.SettleRentalAction && r.TargetItemId == itemId))
            return Result.Success();
        if (!item.IsReadyForSettlement) return Result.Success();
        if (string.IsNullOrWhiteSpace(order.InvoiceRef) || string.IsNullOrWhiteSpace(order.PaymentRef))
            return Error.Conflict("PAYMENT_REFERENCE_MISSING", "InvoiceRef or PaymentRef is not available for settlement.");

        string operationKey = RentalOperationExecutor.ComputeOperationKey(RentalItem.SettleRentalAction, itemId.ToString("N"));
        var request = new SettleRentalRequest(order.Id, item.Id, order.InvoiceRef, order.PaymentRef,
            item.SupplierId, item.DepositAmount, item.FinalDeductionAmount!.Value,
            item.DepositRefundAmount, order.Currency,
            item.DeductionProposal?.Id.ToString("D") ?? $"return:{item.Id:D}", operationKey);
        var valid = request.Validate();
        if (valid.IsFailure) return valid.Error;
        var payload = JsonSerializer.Serialize(new { providerRequestJson = JsonSerializer.Serialize(request) });
        order.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(), order.Id,
            operationKey, RentalItem.SettleRentalAction, RentalOperationExecutor.ComputeFingerprint(request),
            clock.GetUtcNow(), targetItemId: item.Id, payloadJson: payload));
        order.IncrementVersion();
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
