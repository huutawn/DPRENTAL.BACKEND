#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Worker tìm yêu cầu trả đến hạn, sau đó khóa và đọc lại order để áp dụng quy tắc quá hạn xác nhận.
/// Thông báo/quyết định trên yêu cầu trả không thay cho xác nhận đóng allocation từ Inventory.
/// </remarks>
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

    public async Task<Result<int>> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order is null) return 0;

        DateTimeOffset now = timeProvider.GetUtcNow();
        var dueRequests = new List<(ReturnRequest Request, RentalItem Item, List<ReturnRequestUnit> OverdueUnits)>();
        int totalActionableUnits = 0;

        foreach (var item in order.Items)
        {
            foreach (var request in item.ReturnRequests)
            {
                if (request.ConfirmationDeadlineAt > now) continue;

                var overdueUnits = request.Units
                    .Where(u => u.IsActionable && !u.OverdueNotifiedAt.HasValue)
                    .ToList();

                if (overdueUnits.Count > 0)
                {
                    dueRequests.Add((request, item, overdueUnits));
                    totalActionableUnits += overdueUnits.Count;
                }
            }
        }

        if (totalActionableUnits == 0)
        {
            return 0;
        }

        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var (_, _, overdueUnits) in dueRequests)
            {
                foreach (var unit in overdueUnits)
                {
                    unit.SetOverdueNotifiedAt(now);
                }
            }

            order.RecordReadModelUpdate(now);

            foreach (var (request, item, overdueUnits) in dueRequests)
            {
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
                    logger?.LogWarning("Failed to enqueue ReturnConfirmationOverdue event for request {RequestId}: {Error}",
                        request.Id, enqueueResult.Error.Message);
                    return enqueueResult.Error;
                }
            }

            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            logger?.LogWarning(ex, "Failed to process overdue return confirmation for order {OrderId}", order.Id);
            return Error.Failure("RETURN_CONFIRMATION_OVERDUE_FAILED", ex.Message);
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
                        OrderChangeTypes.ReturnConfirmationOverdue,
                        now),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to send ReturnConfirmationOverdue notification to renter {RenterId} for order {OrderId}",
                    order.RenterId, order.Id);
            }

            var distinctSupplierSubOrders = dueRequests
                .Select(r => r.Item.SupplierId)
                .Distinct()
                .Select(supplierId => (SupplierId: supplierId, SubOrder: order.SubOrders.FirstOrDefault(s => s.SupplierId == supplierId)))
                .Where(x => x.SubOrder != null)
                .ToList();

            foreach (var (supplierId, subOrder) in distinctSupplierSubOrders)
            {
                try
                {
                    await notifier.NotifySupplierSubOrderChangedAsync(
                        supplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder!.Id,
                            order.Version,
                            OrderChangeTypes.ReturnConfirmationOverdue,
                            now),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to send ReturnConfirmationOverdue notification to supplier {SupplierId} for sub-order {SubOrderId}",
                        supplierId, subOrder!.Id);
                }
            }
        }

        return totalActionableUnits;
    }

    public async Task<ReturnConfirmationOverdueBatchResult> ProcessBatchAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        var dueOrderIds = await GetDueOrderIdsAsync(batchSize, cancellationToken);
        int processed = 0;
        int warned = 0;
        int failed = 0;

        foreach (var orderId in dueOrderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await ProcessOrderAsync(orderId, cancellationToken);
                if (result.IsSuccess)
                {
                    warned += result.Value;
                    processed++;
                }
                else
                {
                    failed++;
                    logger?.LogWarning("Failed to process overdue return confirmation for order {OrderId}: {Error}", orderId, result.Error);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger?.LogWarning(ex, "Unexpected error processing overdue return confirmation for order {OrderId}", orderId);
            }
        }

        return new ReturnConfirmationOverdueBatchResult(processed, warned, failed);
    }
}
