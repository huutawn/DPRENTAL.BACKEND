#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Expiry;

public sealed class ExpiryWorkflowService : IExpiryWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExpiryWorkflowService> _logger;
    private readonly string _rentalEventsTopic;
    private readonly IRentalEventProducer _rentalEvents;
    public ExpiryWorkflowService(
        IRentalOrderRepository orderRepository,
        IOutboxRepository outboxRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<ExpiryWorkflowService> logger,
        TimeProvider? timeProvider = null,
        IRentalEventProducer? rentalEvents = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
        _rentalEvents = rentalEvents ?? new Dprental.RentalService.Application.Kafka.Producers.RentalEventProducer(
            outboxRepository,
            Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions { RentalEventsTopic = _rentalEventsTopic }),
            _timeProvider);
    }
    public async Task<int> ProcessPaymentTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var expiredOrderIds = await _orderRepository.GetPendingPaymentExpiredOrderIdsAsync(utcNow, 50, cancellationToken);

        int count = 0;
        foreach (var orderId in expiredOrderIds)
        {
            var res = await ExpireSingleOrderPaymentAsync(orderId, cancellationToken);
            if (res.IsSuccess) count++;
        }

        return count;
    }

    public async Task<int> ProcessSupplierTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var expiredItemIds = await _orderRepository.GetPendingSupplierTimeoutItemIdsAsync(utcNow, 50, cancellationToken);

        int count = 0;
        foreach (var itemId in expiredItemIds)
        {
            var res = await ExpireSingleItemSupplierTimeoutAsync(itemId, cancellationToken);
            if (res.IsSuccess) count++;
        }

        return count;
    }

    public async Task<Result> ExpireSingleOrderPaymentAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        string opKey = RentalOperationExecutor.ComputeOperationKey("PaymentExpiry", orderId.ToString());
        return await _operationExecutor.ExecutePaymentExpiryAsync(orderId, opKey, cancellationToken);
    }

    public async Task<Result> ExpireSingleItemSupplierTimeoutAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return Result.Success();

        string? inFlightAcceptOpId = null;
        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken))
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
            if (order == null) return Result.Success();

            var item = order.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || item.Status != RentalItemStatus.PendingSupplierConfirmation)
            {
                return Result.Success();
            }

            if (item.SupplierConfirmationDeadline.HasValue && utcNow < item.SupplierConfirmationDeadline.Value)
            {
                return Result.Success();
            }

            // Check in-flight accept intent created before deadline for reconciliation
            var inFlightAccept = order.OperationReceipts
                .FirstOrDefault(r => r.Action == "SupplierAccept" &&
                                     r.TargetItemId == item.Id &&
                                     (r.Status == RentalOperationReceipt.StatusProcessing || r.Status == RentalOperationReceipt.StatusPending) &&
                                     item.SupplierConfirmationDeadline.HasValue &&
                                     r.CreatedAt < item.SupplierConfirmationDeadline.Value);

            if (inFlightAccept != null)
            {
                inFlightAcceptOpId = inFlightAccept.OperationId;
            }
            else if (order.HasInFlightAccept(item.Id))
            {
                _logger.LogInformation("Item {ItemId} has in-flight accept intent or uncertain outcome; protected from supplier timeout expiry.", itemId);
                return Result.Success();
            }
            else
            {
                string timeoutOpKey = RentalOperationExecutor.ComputeOperationKey("SupplierTimeout", $"{order.Id}:{item.Id}");
                item.ApplySupplierTimeout(utcNow, timeoutOpKey);
                RentalOrderStatusPolicy.Apply(order, utcNow, timeoutOpKey);

                string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
                var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
                if (releaseReceipt == null)
                {
                    var releaseReq = new ReleaseAllocationRequest(releaseOpKey, "Supplier confirmation deadline expired");
                    var relCmd = new RentalOperationCommand(
                        1,
                        "ReleaseAllocation",
                        order.Id,
                        item.Id,
                        releaseOpKey,
                        item.AllocationGroupId,
                        null,
                        JsonSerializer.Serialize(releaseReq, JsonOptions));

                    releaseReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        order.Id,
                        releaseOpKey,
                        "ReleaseAllocation",
                        RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{item.AllocationGroupId}"),
                        isSuccess: false,
                        errorCode: null,
                        errorMessage: null,
                        createdAt: utcNow,
                        status: RentalOperationReceipt.StatusPending,
                        targetItemId: item.Id,
                        payloadJson: JsonSerializer.Serialize(relCmd, JsonOptions));
                    order.AddOperationReceipt(releaseReceipt);
                }

                string refundOpKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
                var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey);
                if (refundReceipt == null)
                {
                    var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                    var refundReq = new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, "Supplier confirmation deadline expired", refundOpKey);
                    var refundCmd = new RentalOperationCommand(
                        1,
                        "RequestRefund",
                        order.Id,
                        item.Id,
                        refundOpKey,
                        null,
                        null,
                        JsonSerializer.Serialize(refundReq, JsonOptions));

                    refundReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        order.Id,
                        refundOpKey,
                        "RequestRefund",
                        RentalOperationExecutor.ComputeFingerprint($"RequestRefund:{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                        isSuccess: false,
                        errorCode: null,
                        errorMessage: null,
                        createdAt: utcNow,
                        status: RentalOperationReceipt.StatusPending,
                        targetItemId: item.Id,
                        prerequisiteOperationId: releaseOpKey,
                        payloadJson: JsonSerializer.Serialize(refundCmd, JsonOptions));
                    order.AddOperationReceipt(refundReceipt);
                }

                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    RentalOrderStatus previousOrderStatus = order.Status;
                    RentalOrderStatusPolicy.Apply(order, utcNow, timeoutOpKey);
                    order.RecordReadModelUpdate(utcNow);

                    var expiredRes = await _rentalEvents.EnqueueRentalItemExpiredAsync(
                        new RentalItemExpiredPayload(order.Id, item.Id, item.SupplierId, utcNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, order.Version),
                        correlationId: timeoutOpKey,
                        cancellationToken: cancellationToken);
                    if (expiredRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return expiredRes.Error;
                    }

                    if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
                    {
                        var orderCompletedRes = await _rentalEvents.EnqueueRentalOrderCompletedAsync(
                            new RentalOrderCompletedPayload(order.Id, order.RenterId, utcNow, order.Version),
                            correlationId: timeoutOpKey,
                            cancellationToken: cancellationToken);
                        if (orderCompletedRes.IsFailure)
                        {
                            await tx.RollbackAsync(cancellationToken);
                            _unitOfWork.DiscardChanges();
                            return orderCompletedRes.Error;
                        }
                    }

                    await _orderRepository.UpdateAsync(order, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    throw;
                }
            }
        }

        if (inFlightAcceptOpId != null)
        {
            var reconcileRes = await _operationExecutor.ExecuteReconcileAcceptAsync(orderId.Value, itemId, inFlightAcceptOpId, cancellationToken);
            if (reconcileRes.IsSuccess)
            {
                _logger.LogInformation("Reconciled in-flight accept intent for item {ItemId} instead of timing out.", itemId);
                return Result.Success();
            }
        }

        return Result.Success();
    }
}
