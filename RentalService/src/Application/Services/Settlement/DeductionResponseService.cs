#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Settlement;

public sealed partial class DeductionResponseService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalMediaClient mediaClient,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<DeductionResponseService> logger) : IDeductionResponseService
{
    private const string RespondAction = "RenterRespondDeduction";

    public async Task<Result<RespondDeductionResponse>> RespondAsync(
        Guid itemId, IRentalCallerContext caller, RespondDeductionRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsService)
            return Error.Forbidden("SERVICE_TOKEN_NOT_ALLOWED", "Service tokens cannot respond for a renter.");
        if (!caller.IsAuthenticated || !caller.UserId.HasValue || caller.UserId == Guid.Empty)
            return Error.Unauthorized("AUTH_REQUIRED", "Renter authentication is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");
        if (request is null || !Enum.IsDefined(request.Decision))
            return Error.Validation("INVALID_RESPONSE", "Decision must be Accept or Dispute.");

        Guid renterId = caller.UserId.Value;
        var orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;

        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (order is null || item is null) return RentalErrors.ItemNotFound;
        var authorization = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        if (authorization.IsFailure) return authorization.Error;

        string key = idempotencyKey.Trim();
        var mediaIds = (request.EvidenceMediaIds ?? []).ToList();
        if (request.Decision == RenterDeductionDecision.Accept &&
            (!string.IsNullOrWhiteSpace(request.DisputeReason) || mediaIds.Count > 0))
            return Error.Validation("INVALID_RESPONSE", "Accept response cannot include dispute reason or evidence.");
        if (request.Decision == RenterDeductionDecision.Dispute &&
            (string.IsNullOrWhiteSpace(request.DisputeReason) || request.DisputeReason.Trim().Length > 2000 ||
             mediaIds.Count > 10 || mediaIds.Contains(Guid.Empty) || mediaIds.Distinct().Count() != mediaIds.Count))
            return Error.Validation("INVALID_DISPUTE", "A reason (maximum 2000 characters) and at most 10 distinct evidence IDs are required.");
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            RenterId = renterId,
            request.Decision,
            Reason = request.DisputeReason?.Trim(),
            MediaIds = mediaIds.OrderBy(id => id)
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (receipt is not null)
        {
            if (receipt.Action != RespondAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            if (!receipt.IsSuccess || string.IsNullOrWhiteSpace(receipt.ApiResponseJson))
                return Error.Conflict("RESPONSE_IN_PROGRESS", "The response is not complete.");
            var saved = JsonSerializer.Deserialize<RespondDeductionResponse>(receipt.ApiResponseJson);
            return saved is null ? Error.Failure("INVALID_RESPONSE_RECEIPT", "Stored response is unavailable.") : saved;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();   // sau khi có khóa

        Result applied;
        if (request.Decision == RenterDeductionDecision.Accept)
        {
            applied = item.AcceptDeductionByRenter(renterId, now, key);
        }
        else
        {
            if (mediaIds.Count > 0)
            {
                var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                    mediaIds, renterId, mediaClient, ct: cancellationToken);
                if (evidence.IsFailure) return evidence.Error;
            }
            applied = item.DisputeDeductionByRenter(renterId, request.DisputeReason, mediaIds, now, key);
        }
        if (applied.IsFailure) return applied.Error;

        var proposal = item.DeductionProposal!;
        var response = new RespondDeductionResponse(
            proposal.Id, item.Id, proposal.Status.ToString(), item.FinalDeductionAmount, now, item.Status.ToString());

        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, key, RespondAction, fingerprint, true, null, null, now,
            targetItemId: item.Id, payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));

        order.IncrementVersion();
        try
        {
            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            unitOfWork.DiscardChanges();
            throw;
        }
        return response;
    }

    public async Task<int> ProcessRenterResponseTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        var itemIds = await orders.GetRenterResponseTimeoutItemIdsAsync(
            timeProvider.GetUtcNow(),
            configuration.GetValue("Workers:RenterResponseDeadline:BatchSize", 50),
            cancellationToken);

        int count = 0;
        foreach (Guid itemId in itemIds)
        {
            try
            {
                var result = await ExpireSingleRenterResponseAsync(itemId, cancellationToken);
                if (result.IsSuccess) count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Renter response timeout lỗi cho item {ItemId}", itemId);
            }
        }
        return count;
    }

    public async Task<Result> ExpireSingleRenterResponseAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return Result.Success();

        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (order is null || item is null) return Result.Success();

        DateTimeOffset now = timeProvider.GetUtcNow();   // sau khi có khóa
        string opKey = RentalOperationExecutor.ComputeOperationKey("RenterResponseTimeout", $"{order.Id}:{item.Id}");

        if (!item.TryAutoAcceptDeduction(now, opKey)) return Result.Success();

        order.IncrementVersion();
        try
        {
            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            unitOfWork.DiscardChanges();
            throw;
        }

        logger.LogInformation("Renter response deadline expired for item {ItemId}; proposal auto-accepted", itemId);
        return Result.Success();
    }
}
