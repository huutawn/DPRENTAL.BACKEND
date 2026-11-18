#pragma warning disable CA1848
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Services.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Settlement;

public sealed partial class DeductionResponseService
{
    public async Task<int> ProcessDisputeHoldTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        int holdDays = configuration.GetValue<int>("Rental:Deduction:OverCapHoldDays", 7);
        if (holdDays is < 1 or > 365)
            throw new InvalidOperationException("Rental:Deduction:OverCapHoldDays must be between 1 and 365.");

        DateTimeOffset cutoff = timeProvider.GetUtcNow().AddDays(-holdDays);
        var ids = await orders.GetDisputeHoldTimeoutItemIdsAsync(cutoff,
            configuration.GetValue("Workers:DeductionDeadline:BatchSize", 50), cancellationToken);
        int processed = 0;
        foreach (Guid itemId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await FinalizeDisputeHoldAsync(itemId, holdDays, cancellationToken)) processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dispute hold timeout failed for rental item {ItemId}", itemId);
            }
        }
        return processed;
    }

    private async Task<bool> FinalizeDisputeHoldAsync(Guid itemId, int holdDays, CancellationToken ct)
    {
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, ct);
        if (!orderId.HasValue) return false;

        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), ct);
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, ct);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (order is null || item is null) return false;

        DateTimeOffset now = timeProvider.GetUtcNow();
        string key = RentalOperationExecutor.ComputeOperationKey("DisputeHoldTimeout", $"{order.Id}:{itemId}");
        if (!item.TryFinalizeDisputeAfterHold(now, holdDays, key)) return false;

        RentalOrderStatusPolicy.Apply(order, now, key);
        order.RecordReadModelUpdate(now);
        try
        {
            await orders.UpdateAsync(order, ct);
            await unitOfWork.SaveChangesAsync(ct);
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
                        OrderChangeTypes.SettlementChanged, now), ct);
                var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
                if (subOrder is not null)
                    await notifier.NotifySupplierSubOrderChangedAsync(item.SupplierId,
                        new SupplierSubOrderChangedNotification(Guid.NewGuid(), order.Id,
                            subOrder.Id, order.Version, OrderChangeTypes.SettlementChanged, now), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dispute hold notification failed for item {ItemId}", itemId);
            }
        }
        return true;
    }
}
