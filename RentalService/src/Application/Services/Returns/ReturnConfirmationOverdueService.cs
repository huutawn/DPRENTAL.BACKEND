#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

public sealed class ReturnConfirmationOverdueService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalEventProducer rentalEvents,
    TimeProvider timeProvider,
    IOrderHubNotifier? notifier = null,
    ILogger<ReturnConfirmationOverdueService>? logger = null) : IReturnConfirmationOverdueService
{
    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return orders.GetReturnConfirmationOverdueOrderIdsAsync(now, batchSize, cancellationToken);
    }

    public async Task<int> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order is null) return 0;

        DateTimeOffset now = timeProvider.GetUtcNow();
        int totalWarnedUnits = 0;
        bool orderModified = false;
        var notificationsToSend = new List<(Guid RenterId, Guid SupplierId, Guid SubOrderId)>();

        foreach (var item in order.Items)
        {
            foreach (var request in item.ReturnRequests)
            {
                if (request.ConfirmationDeadlineAt > now) continue;

                var overdueUnits = request.Units
                    .Where(u => u.IsActionable && !u.OverdueNotifiedAt.HasValue)
                    .ToList();

                if (overdueUnits.Count == 0) continue;

                await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    foreach (var unit in overdueUnits)
                    {
                        unit.SetOverdueNotifiedAt(now);
                    }

                    order.RecordReadModelUpdate(now);

                    string correlationId = $"return-overdue:{request.Id}:{now.ToUnixTimeSeconds()}";
                    var enqueueResult = await rentalEvents.EnqueueReturnConfirmationOverdueAsync(
                        new ReturnConfirmationOverduePayload(
                            order.Id,
                            item.Id,
                            request.Id,
                            item.SupplierId,
                            order.RenterId,
                            request.ConfirmationDeadlineAt,
                            now,
                            overdueUnits.Select(u => u.RentalUnitAssignmentId).ToArray(),
                            order.Version),
                        correlationId: correlationId,
                        cancellationToken: cancellationToken);

                    if (enqueueResult.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        unitOfWork.DiscardChanges();
                        logger?.LogWarning("Failed to enqueue ReturnConfirmationOverdue event: {Error}", enqueueResult.Error.Message);
                        continue;
                    }

                    await orders.UpdateAsync(order, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);

                    totalWarnedUnits += overdueUnits.Count;
                    orderModified = true;

                    var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
                    if (subOrder != null)
                    {
                        notificationsToSend.Add((order.RenterId, item.SupplierId, subOrder.Id));
                    }
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    unitOfWork.DiscardChanges();
                    logger?.LogWarning(ex, "Failed to process overdue return confirmation for request {RequestId}", request.Id);
                    throw;
                }
            }
        }

        if (notifier != null && orderModified)
        {
            foreach (var (renterId, supplierId, subOrderId) in notificationsToSend)
            {
                try
                {
                    await notifier.NotifyRenterOrderChangedAsync(
                        renterId,
                        new RenterOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            order.Version,
                            OrderChangeTypes.ReturnConfirmationOverdue,
                            now),
                        cancellationToken);

                    await notifier.NotifySupplierSubOrderChangedAsync(
                        supplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrderId,
                            order.Version,
                            OrderChangeTypes.ReturnConfirmationOverdue,
                            now),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Failed to send ReturnConfirmationOverdue notification for order {OrderId}", order.Id);
                }
            }
        }

        return totalWarnedUnits;
    }

    public async Task<ReturnConfirmationOverdueBatchResult> ProcessBatchAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        var dueOrderIds = await GetDueOrderIdsAsync(batchSize, cancellationToken);
        int processed = 0;
        int warned = 0;
        int failed = 0;

        foreach (var orderId in dueOrderIds)
        {
            try
            {
                int count = await ProcessOrderAsync(orderId, cancellationToken);
                warned += count;
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger?.LogWarning(ex, "Error processing overdue return confirmation for order {OrderId}", orderId);
            }
        }

        return new ReturnConfirmationOverdueBatchResult(processed, warned, failed);
    }
}
