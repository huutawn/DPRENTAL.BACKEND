#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Expiry;

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

    public ExpiryWorkflowService(
        IRentalOrderRepository orderRepository,
        IOutboxRepository outboxRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<ExpiryWorkflowService> logger,
        TimeProvider? timeProvider = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
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

        var order = await _orderRepository.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        if (order == null) return Result.Success();

        string? inFlightAcceptOpId = null;
        string? releaseOpKey = null;

        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(order.Id, TimeSpan.FromSeconds(5), cancellationToken))
        {
            order = await _orderRepository.GetByIdWithDetailsAsync(order.Id, cancellationToken);
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

            var inFlightAccept = order.OperationReceipts
                .FirstOrDefault(r => r.Action == "SupplierAccept" &&
                                     r.TargetItemId == item.Id &&
                                     r.Status == RentalOperationReceipt.StatusProcessing &&
                                     item.SupplierConfirmationDeadline.HasValue &&
                                     r.CreatedAt < item.SupplierConfirmationDeadline.Value);

            if (inFlightAccept != null)
            {
                inFlightAcceptOpId = inFlightAccept.OperationId;
            }
            else
            {
                string timeoutOpKey = RentalOperationExecutor.ComputeOperationKey("SupplierTimeout", $"{order.Id}:{item.Id}");
                item.ApplySupplierTimeout(utcNow, timeoutOpKey);
                order.EvaluateOrderStatusRollup(utcNow, timeoutOpKey);

                releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
                var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
                if (releaseReceipt == null)
                {
                    releaseReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        order.Id,
                        releaseOpKey,
                        "ReleaseAllocation",
                        RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"),
                        isSuccess: false,
                        errorCode: null,
                        errorMessage: null,
                        createdAt: utcNow,
                        status: RentalOperationReceipt.StatusPending,
                        targetItemId: item.Id,
                        payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseOpKey, "Supplier confirmation deadline expired"), JsonOptions));
                    order.AddOperationReceipt(releaseReceipt);
                }

                string refundOpKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
                var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey);
                if (refundReceipt == null)
                {
                    var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                    refundReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        order.Id,
                        refundOpKey,
                        "RequestRefund",
                        RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                        isSuccess: false,
                        errorCode: null,
                        errorMessage: null,
                        createdAt: utcNow,
                        status: RentalOperationReceipt.StatusPending,
                        targetItemId: item.Id,
                        prerequisiteOperationId: releaseOpKey,
                        payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, "Supplier confirmation deadline expired", refundOpKey), JsonOptions));
                    order.AddOperationReceipt(refundReceipt);
                }

                var outboxEnvelope = new RentalEventEnvelope<RentalItemExpiredPayload>(
                    Guid.NewGuid(),
                    RentalEventTypes.RentalItemExpired,
                    1,
                    "rental-service",
                    order.Id.ToString("D"),
                    utcNow,
                    timeoutOpKey,
                    null,
                    new RentalItemExpiredPayload(order.Id, item.Id, item.SupplierId, utcNow, "Supplier confirmation deadline expired without decision"));

                var outbox = new RentalOutboxMessage(
                    outboxEnvelope.MessageId,
                    _rentalEventsTopic,
                    order.Id.ToString("D"),
                    RentalEventTypes.RentalItemExpired,
                    JsonSerializer.Serialize(outboxEnvelope, JsonOptions),
                    utcNow);

                await _outboxRepository.AddAsync(outbox, cancellationToken);
                order.IncrementVersion();
                await _orderRepository.UpdateAsync(order, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        if (inFlightAcceptOpId != null)
        {
            var reconcileRes = await _operationExecutor.ExecuteReconcileAcceptAsync(order.Id, itemId, inFlightAcceptOpId, cancellationToken);
            if (reconcileRes.IsSuccess)
            {
                _logger.LogInformation("Reconciled in-flight accept intent for item {ItemId} instead of timing out.", itemId);
                return Result.Success();
            }
        }
        else if (releaseOpKey != null)
        {
            _ = _operationExecutor.ExecuteReleaseAllocationAsync(order.Id, itemId, releaseOpKey, cancellationToken);
        }

        return Result.Success();
    }
}
