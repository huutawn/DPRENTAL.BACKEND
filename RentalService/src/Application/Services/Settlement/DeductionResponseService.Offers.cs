using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1848

namespace Dprental.RentalService.Application.Services.Settlement;

public sealed partial class DeductionResponseService
{
    public async Task<Result<OfferDisputeAmountResponse>> OfferDisputeAmountAsync(Guid itemId,
        IRentalCallerContext caller, OfferDisputeAmountRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        if (supplier.IsFailure) return supplier.Error;
        if (request is null || string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_OFFER_REQUEST", "Offer and Idempotency-Key are required.");

        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value,
            TimeSpan.FromSeconds(5), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null) return RentalErrors.ItemNotFound;
        var owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(supplier.Value,
            order.Items.FirstOrDefault(i => i.Id == itemId));
        if (owned.IsFailure) return owned.Error;
        var item = owned.Value;

        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new { ItemId = itemId, SupplierId = supplier.Value, request.Amount });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var existing = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (existing is not null)
        {
            if (existing.Action != "SupplierDisputeOffer" || existing.TargetItemId != itemId ||
                existing.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(existing.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<OfferDisputeAmountResponse>(existing.ApiResponseJson);
            return saved is null ? Error.Conflict("OFFER_IN_PROGRESS", "The offer is not complete.") : saved;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        int extensionHours = configuration.GetValue("Rental:Deduction:DisputeExtensionHours", 12);
        var offered = item.OfferDisputeAmountBySupplier(supplier.Value, request.Amount,
            now, extensionHours, key);
        if (offered.IsFailure) return offered.Error;

        var response = new OfferDisputeAmountResponse(item.Id, request.Amount, now,
            item.DeductionProposal!.RenterResponseDeadline.AddHours(extensionHours));
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key,
            "SupplierDisputeOffer", fingerprint, true, null, null, now, targetItemId: item.Id,
            payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));
        RentalOrderStatusPolicy.Apply(order, now, key);
        order.RecordReadModelUpdate(now);
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
        if (notifier is not null)
        {
            try
            {
                await notifier.NotifyRenterOrderChangedAsync(order.RenterId,
                    new RenterOrderChangedNotification(Guid.NewGuid(), order.Id, order.Version,
                        OrderChangeTypes.SettlementChanged, now), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dispute offer notification failed for item {ItemId}", itemId);
            }
        }
        return response;
    }
}
