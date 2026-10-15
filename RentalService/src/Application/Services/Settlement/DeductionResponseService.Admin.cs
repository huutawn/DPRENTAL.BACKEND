using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Services.Settlement;

public sealed partial class DeductionResponseService
{
    public async Task<Result<ResolveDisputeResponse>> ResolveDisputeAsync(
        Guid itemId, IRentalCallerContext caller, ResolveDisputeRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeResolution(caller);
        if (auth.IsFailure) return auth.Error;
        if (request is null || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_REQUEST", "Request and Idempotency-Key (maximum 64 characters) are required.");
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (order is null || item is null) return RentalErrors.ItemNotFound;
        var mediaIds = (request.ResolutionMediaIds ?? []).ToArray();
        string payload = JsonSerializer.Serialize(new { ItemId = itemId, AdminId = caller.UserId,
            request.FinalDeductionAmount, Note = request.DecisionNote?.Trim(),
            MediaIds = mediaIds.OrderBy(id => id) });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        string key = idempotencyKey.Trim();
        var existing = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (existing is not null)
        {
            if (existing.Action != "AdminResolveDispute" || existing.TargetItemId != itemId ||
                existing.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(existing.ApiResponseJson)
                ? null : JsonSerializer.Deserialize<ResolveDisputeResponse>(existing.ApiResponseJson);
            return saved is null ? Error.Conflict("RESOLUTION_IN_PROGRESS", "Resolution is not complete.") : saved;
        }
        var proposalForValidation = item.DeductionProposal;
        if (proposalForValidation?.Dispute is null ||
            proposalForValidation.Status != Dprental.RentalService.Common.Enums.DeductionProposalStatus.Disputed)
            return Error.Conflict("NO_OPEN_DISPUTE", "No open dispute exists for this rental item.");
        if (request.FinalDeductionAmount < 0 ||
            request.FinalDeductionAmount > proposalForValidation.TotalProposedAmount ||
            request.FinalDeductionAmount > item.DepositAmount ||
            decimal.Round(request.FinalDeductionAmount, 2) != request.FinalDeductionAmount ||
            string.IsNullOrWhiteSpace(request.DecisionNote) || request.DecisionNote.Trim().Length > 2000 ||
            mediaIds.Length > 10 || mediaIds.Contains(Guid.Empty) || mediaIds.Distinct().Count() != mediaIds.Length)
            return Error.Validation("INVALID_DISPUTE_RESOLUTION", "Final amount, decision note or evidence is invalid.");
        var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
            mediaIds, caller.UserId!.Value, mediaClient, ct: cancellationToken);
        if (evidence.IsFailure) return evidence.Error;
        DateTimeOffset now = timeProvider.GetUtcNow();
        var resolved = item.ResolveDeductionDispute(caller.UserId.Value, request.FinalDeductionAmount,
            request.DecisionNote, mediaIds, now, key);
        if (resolved.IsFailure) return resolved.Error;
        var proposal = item.DeductionProposal!;
        string decision = request.FinalDeductionAmount == proposal.TotalProposedAmount ? "UpheldInFull"
            : request.FinalDeductionAmount == 0 ? "Rejected" : "Partial";
        var response = new ResolveDisputeResponse(proposal.Id, item.Id,
            request.FinalDeductionAmount, decision, now, item.Status.ToString());
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key,
            "AdminResolveDispute", fingerprint, true, null, null, now, targetItemId: itemId,
            payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));
        order.IncrementVersion();
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return response;
    }

    public async Task<Result<DisputeDetailDto>> GetDisputeDetailAsync(
        Guid itemId, IRentalCallerContext caller, CancellationToken cancellationToken = default)
    {
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeResolution(caller);
        if (auth.IsFailure) return auth.Error;
        var order = await orders.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        var proposal = item?.DeductionProposal;
        var dispute = proposal?.Dispute;
        if (order is null || item is null || proposal is null || dispute is null)
            return RentalErrors.DisputeNotFound;
        var units = item.UnitAssignments.Select(u => new DisputeUnitEvidenceDto(
            u.Id, u.AssetId, u.Status.ToString(), u.HandoverCondition?.ToString(),
            u.HandoverNote, u.HandoverMediaIds, u.ReturnCondition?.ToString(),
            u.ReturnNote, u.ReturnMediaIds, u.DeductionProposalId == proposal.Id,
            u.DeductionReason, u.DeductionMediaIds)).ToArray();
        return new DisputeDetailDto(item.Id, order.Id, order.OrderCode, item.SupplierId, order.RenterId,
            item.DepositAmount, proposal.TotalProposedAmount, proposal.DamageDescription,
            proposal.SubmittedAt, proposal.Status.ToString(), dispute.Reason, dispute.MediaIds,
            proposal.RespondedAt, units);
    }

    public async Task<Result<IReadOnlyList<DisputeSummaryDto>>> GetOpenDisputesAsync(
        IRentalCallerContext caller, int skip, int take, CancellationToken cancellationToken = default)
    {
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeResolution(caller);
        if (auth.IsFailure) return auth.Error;
        if (skip < 0 || take is < 1 or > 100)
            return Error.Validation("INVALID_PAGINATION", "skip must be nonnegative; take must be 1 through 100.");
        var ids = await orders.GetDisputedItemIdsAsync(skip, take, cancellationToken);
        var rows = new List<DisputeSummaryDto>(ids.Count);
        foreach (var id in ids)
        {
            var order = await orders.GetByItemIdWithDetailsAsync(id, cancellationToken);
            var item = order?.Items.FirstOrDefault(i => i.Id == id);
            var proposal = item?.DeductionProposal;
            if (order is null || item is null || proposal?.Dispute is null) continue;
            rows.Add(new DisputeSummaryDto(item.Id, order.Id, order.OrderCode,
                proposal.TotalProposedAmount, item.DepositAmount, proposal.RespondedAt));
        }
        return rows;
    }
}
