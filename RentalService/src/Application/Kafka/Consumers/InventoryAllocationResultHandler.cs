#pragma warning disable CA1848, CA1873
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Kafka.Consumers;

public sealed class InventoryAllocationResultHandler : IInventoryAllocationResultHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalCheckoutOperationRepository? _checkoutOperationRepo;
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IInventoryCommandProducer? _inventoryCommands;
    private readonly ICheckoutOrderWriter? _orderWriter;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor? _operationExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InventoryAllocationResultHandler> _logger;

    public InventoryAllocationResultHandler(
        IRentalOrderRepository orderRepository,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        IOrderLockManager lockManager,
        TimeProvider? timeProvider = null,
        IRentalCheckoutOperationRepository? checkoutOperationRepo = null,
        IInventoryCommandProducer? inventoryCommands = null,
        ICheckoutOrderWriter? orderWriter = null,
        IRentalOperationExecutor? operationExecutor = null,
        ILogger<InventoryAllocationResultHandler>? logger = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkoutOperationRepo = checkoutOperationRepo;
        _inventoryCommands = inventoryCommands;
        _orderWriter = orderWriter;
        _operationExecutor = operationExecutor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InventoryAllocationResultHandler>.Instance;
    }

    public async Task<Result> HandleAllocationEventAsync(
        Guid orderId,
        RentalInboxMessage msg,
        RentalOrder? order,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(msg.Payload);
        var root = doc.RootElement;

        // Envelope validation
        if (!root.TryGetProperty("payload", out var payloadElement))
        {
            return Error.Validation("INVALID_ENVELOPE", "Missing payload property in allocation event envelope.");
        }

        string commandType = payloadElement.TryGetProperty("commandType", out var ctProp) ? ctProp.GetString() ?? string.Empty : string.Empty;
        string operationKey = payloadElement.TryGetProperty("operationKey", out var okProp) ? okProp.GetString() ?? string.Empty : string.Empty;
        Guid rentalItemId = payloadElement.TryGetProperty("rentalItemId", out var riProp) && riProp.TryGetGuid(out var ri) ? ri : Guid.Empty;

        // Skip return events: Return consumer handles ReturnedUnitsApplied/Rejected
        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.ApplyReturnedUnits, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandType, "MarkAssetsReturned", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandType, "BlockDamagedAssets", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Skipping return-related command {CommandType} in allocation handler", commandType);
            return Result.Success();
        }

        bool isSuccess = string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, StringComparison.OrdinalIgnoreCase);
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.CreateHold, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleCreateHoldResultAsync(orderId, rentalItemId, operationKey, isSuccess, payloadElement, utcNow, cancellationToken);
        }

        // For non-CreateHold commands, an existing order is required
        if (order == null)
        {
            return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{orderId}'.");
        }

        var receipt = order.OperationReceipts.FirstOrDefault(r => string.Equals(r.OperationId, operationKey, StringComparison.OrdinalIgnoreCase));
        if (receipt == null)
        {
            _logger.LogWarning("Receipt for operationKey {OperationKey} not found on order {OrderId}", operationKey, orderId);
            return Result.Success();
        }

        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.ConfirmAllocation, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleConfirmAllocationResultAsync(order, receipt, rentalItemId, operationKey, isSuccess, payloadElement, utcNow, cancellationToken);
        }

        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.ExtendHold, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleExtendHoldResultAsync(order, receipt, rentalItemId, operationKey, isSuccess, payloadElement, utcNow, cancellationToken);
        }

        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.ReleaseAllocation, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleReleaseAllocationResultAsync(order, receipt, rentalItemId, isSuccess, payloadElement, utcNow, cancellationToken);
        }

        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.MarkAssetsHandedOver, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleMarkAssetsHandedOverResultAsync(order, receipt, rentalItemId, operationKey, isSuccess, payloadElement, utcNow, cancellationToken);
        }

        _logger.LogWarning("Unhandled allocation command type: {CommandType}", commandType);
        return Result.Success();
    }

    private async Task<Result> HandleCreateHoldResultAsync(
        Guid orderId,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        JsonElement payloadElement,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (_checkoutOperationRepo == null || _inventoryCommands == null || _orderWriter == null)
        {
            _logger.LogWarning("Checkout dependencies not configured on allocation handler; skipping CreateHold result");
            return Result.Success();
        }

        var op = await _checkoutOperationRepo.GetByExpectedOrderIdAsync(orderId, cancellationToken);
        if (op == null)
        {
            _logger.LogWarning("Checkout operation for order {OrderId} not found; skipping", orderId);
            return Result.Success();
        }

        if (op.Status == CheckoutOperationStatus.Succeeded)
        {
            _logger.LogInformation("Checkout operation {OperationId} already succeeded; duplicate reply ignored without release", op.OperationId);
            return Result.Success();
        }

        if (op.Status is CheckoutOperationStatus.Failed or CheckoutOperationStatus.TimedOut)
        {
            if (isSuccess)
            {
                var releaseCmd = new ReleaseAllocationCommandPayload(
                    orderId,
                    rentalItemId,
                    $"checkout:late:{orderId:N}:{rentalItemId:N}:release",
                    "Late hold response after checkout failed/timed out");

                await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: orderId.ToString("D"), cancellationToken: cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            return Result.Success();
        }

        if (op.HoldExpiresAt <= utcNow)
        {
            op.MarkTimedOut(utcNow);
            var lines = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions);
            if (lines != null)
            {
                foreach (var l in lines)
                {
                    var releaseCmd = new ReleaseAllocationCommandPayload(
                        orderId,
                        l.RentalItemId,
                        $"checkout:timeout:{orderId:N}:{l.RentalItemId:N}:release",
                        "Checkout timed out before all holds completed");
                    await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: orderId.ToString("D"), cancellationToken: cancellationToken);
                }
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        if (!isSuccess)
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "HOLD_REJECTED" : "HOLD_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Hold rejected by Inventory" : "Hold rejected";

            op.MarkFailed(errCode, errMsg, utcNow);

            var lines = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions);
            if (lines != null)
            {
                foreach (var l in lines)
                {
                    var releaseCmd = new ReleaseAllocationCommandPayload(
                        orderId,
                        l.RentalItemId,
                        $"checkout:compensation:{orderId:N}:{l.RentalItemId:N}:release",
                        $"Hold rejected on line {rentalItemId}: {errMsg}");
                    await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: orderId.ToString("D"), cancellationToken: cancellationToken);
                }
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var linesMap = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions) ?? new();
        var targetLine = linesMap.FirstOrDefault(l => l.RentalItemId == rentalItemId);
        if (targetLine == null)
        {
            _logger.LogWarning("Line for rentalItemId {RentalItemId} not found in operation {OperationId}", rentalItemId, op.OperationId);
            return Result.Success();
        }

        var plan = JsonSerializer.Deserialize<CheckoutPlan>(op.PreparationSnapshotJson, JsonOptions)!;
        var planLine = plan.Lines.FirstOrDefault(pl => pl.CartItemId == targetLine.CartItemId);
        int expectedQuantity = planLine?.Quantity ?? 1;

        // Validate allocations from Inventory
        if (!payloadElement.TryGetProperty("allocations", out var alProp) || alProp.ValueKind != JsonValueKind.Array)
        {
            op.MarkFailed("INVALID_ALLOCATIONS_PAYLOAD", "Missing allocations array from Inventory", utcNow);
            foreach (var l in linesMap)
            {
                var releaseCmd = new ReleaseAllocationCommandPayload(
                    orderId, l.RentalItemId, $"checkout:compensation:{orderId:N}:{l.RentalItemId:N}:release", "Invalid allocations payload");
                await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: orderId.ToString("D"), cancellationToken: cancellationToken);
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Error.Validation("INVALID_ALLOCATIONS_PAYLOAD", "Missing allocations array from Inventory");
        }

        var allocs = JsonSerializer.Deserialize<List<AllocationAssetMappingPayload>>(alProp.GetRawText(), JsonOptions);
        if (allocs == null || allocs.Count != expectedQuantity ||
            allocs.Select(a => a.AllocationId).Distinct().Count() != allocs.Count ||
            allocs.Select(a => a.AssetId).Distinct().Count() != allocs.Count ||
            allocs.Any(a => a.AllocationId == Guid.Empty || a.AssetId == Guid.Empty))
        {
            op.MarkFailed("ALLOCATIONS_VALIDATION_FAILED", "Allocations quantity mismatch or duplicate IDs from Inventory", utcNow);
            foreach (var l in linesMap)
            {
                var releaseCmd = new ReleaseAllocationCommandPayload(
                    orderId, l.RentalItemId, $"checkout:compensation:{orderId:N}:{l.RentalItemId:N}:release", "Allocations validation failed");
                await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: orderId.ToString("D"), cancellationToken: cancellationToken);
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Error.Validation("ALLOCATIONS_VALIDATION_FAILED", "Allocations quantity mismatch or duplicate IDs from Inventory");
        }

        linesMap.Remove(targetLine);
        linesMap.Add(targetLine with { Status = "Held", Allocations = allocs });

        bool allHeld = linesMap.All(l => l.Status == "Held");
        if (allHeld)
        {
            var heldLines = linesMap.Select(l =>
            {
                var pl = plan.Lines.First(p => p.CartItemId == l.CartItemId);
                var assignedAssets = (l.Allocations ?? (IReadOnlyList<AllocationAssetMappingPayload>)Array.Empty<AllocationAssetMappingPayload>())
                    .Select(a => new AssignedAssetDto(a.AllocationId, a.AssetId, "Held"))
                    .ToList();

                return new CheckoutHeldLine(
                    pl,
                    l.RentalItemId,
                    $"rental-item:{l.RentalItemId}",
                    assignedAssets);
            }).ToList();

            bool hasSavepoint = false;
            if (_unitOfWork is DbContext dbCtx && dbCtx.Database.IsRelational() && dbCtx.Database.CurrentTransaction is not null)
            {
                await dbCtx.Database.ExecuteSqlRawAsync("SAVEPOINT sp_finalize_order;", cancellationToken);
                hasSavepoint = true;
            }

            Result<RentalOrder> orderResult;
            try
            {
                orderResult = await _orderWriter.CreateOrderAsync(plan, heldLines, op.OperationId, op.ExpectedRentalOrderId, op.HoldExpiresAt, utcNow, cancellationToken);
            }
            catch (Exception ex)
            {
                if (hasSavepoint && _unitOfWork is DbContext db)
                {
                    try { await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT sp_finalize_order;", CancellationToken.None); } catch { }
                }
                _logger.LogError(ex, "Lỗi hạ tầng khi lưu đơn hàng cho operation {OperationId}", op.OperationId);
                throw;
            }

            if (orderResult.IsFailure)
            {
                if (hasSavepoint && _unitOfWork is DbContext db)
                {
                    await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT sp_finalize_order;", cancellationToken);
                }
                _unitOfWork.DiscardChanges();

                op.MarkFailed(orderResult.Error.Code, orderResult.Error.Message, utcNow);
                foreach (var l in linesMap)
                {
                    var releaseCmd = new ReleaseAllocationCommandPayload(
                        orderId,
                        l.RentalItemId,
                        $"checkout:compensation:{orderId:N}:{l.RentalItemId:N}:release",
                        $"Failed to create order: {orderResult.Error.Message}");
                    await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: orderId.ToString("D"), cancellationToken: cancellationToken);
                }
                await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return orderResult.Error;
            }

            var createdOrder = orderResult.Value;
            string invoiceOpKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", createdOrder.Id.ToString());
            var invoiceReceipt = RentalOperationReceipt.CreatePending(
                Guid.NewGuid(),
                createdOrder.Id,
                invoiceOpKey,
                "CreateInvoice",
                RentalOperationExecutor.ComputeFingerprint($"CreateInvoice:{createdOrder.Id}"),
                utcNow,
                targetItemId: null,
                payloadJson: null);

            createdOrder.AddOperationReceipt(invoiceReceipt);
            op.MarkSucceeded(utcNow, invoiceStatus: "Pending");
            await _orderRepository.UpdateAsync(createdOrder, cancellationToken);
            await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else
        {
            op.UpdateItemHoldStatuses(JsonSerializer.Serialize(linesMap, JsonOptions), utcNow);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    private async Task<Result> HandleConfirmAllocationResultAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        JsonElement payloadElement,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            receipt.MarkSucceeded(utcNow, null, payloadElement.GetRawText());

            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            if (item != null)
            {
                // Invariant: Do not restore Rejected or Expired items!
                if (item.Status == RentalItemStatus.PendingSupplierConfirmation)
                {
                    item.ApplySupplierAccept(item.SupplierId, utcNow, operationKey, operationKey);
                    order.EvaluateOrderStatusRollup(utcNow, operationKey);

                    var outboxPayload = new RentalItemConfirmedPayload(
                        order.Id,
                        item.Id,
                        item.SupplierId,
                        utcNow,
                        operationKey);

                    var envelope = new KafkaMessageEnvelope<RentalItemConfirmedPayload>(
                        Guid.NewGuid(),
                        KafkaContractSpec.EventTypes.RentalItemConfirmed,
                        KafkaContractSpec.CurrentEnvelopeVersion,
                        KafkaContractSpec.Producers.RentalService,
                        order.Id.ToString("D"),
                        utcNow,
                        operationKey,
                        null,
                        outboxPayload);

                    var outbox = new RentalOutboxMessage(
                        envelope.MessageId,
                        KafkaContractSpec.Topics.RentalEvents,
                        order.Id.ToString("D"),
                        KafkaContractSpec.EventTypes.RentalItemConfirmed,
                        JsonSerializer.Serialize(envelope, JsonOptions),
                        utcNow);

                    await _outboxRepository.AddAsync(outbox, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("ConfirmAllocation succeeded late for item {ItemId} in status {Status}; not restoring", item.Id, item.Status);
                }
            }
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "COMMAND_REJECTED" : "COMMAND_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Rejected by Inventory" : "Rejected";
            receipt.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> HandleExtendHoldResultAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        JsonElement payloadElement,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            receipt.MarkSucceeded(utcNow, null, payloadElement.GetRawText());

            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            if (item != null)
            {
                var notifyEnvelope = new KafkaMessageEnvelope<RentalSupplierConfirmationRequestedPayload>(
                    Guid.NewGuid(),
                    KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
                    1,
                    KafkaContractSpec.Producers.RentalService,
                    order.Id.ToString("D"),
                    utcNow,
                    receipt.OperationId,
                    null,
                    new RentalSupplierConfirmationRequestedPayload(
                        order.Id,
                        item.Id,
                        item.SupplierId,
                        item.EquipmentListingId,
                        item.Quantity,
                        item.SupplierConfirmationDeadline ?? utcNow.AddHours(24)));

                var outbox = new RentalOutboxMessage(
                    notifyEnvelope.MessageId,
                    KafkaContractSpec.Topics.RentalEvents,
                    order.Id.ToString("D"),
                    KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
                    JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
                    utcNow);

                await _outboxRepository.AddAsync(outbox, cancellationToken);
            }
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "HOLD_EXTENSION_REJECTED" : "HOLD_EXTENSION_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Hold extension rejected" : "Rejected";
            receipt.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> HandleReleaseAllocationResultAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        bool isSuccess,
        JsonElement payloadElement,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            receipt.MarkSucceeded(utcNow, null, payloadElement.GetRawText());
            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            item?.MarkAssignmentsReleased();

            string refundOpKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{rentalItemId}");
            var pendingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey && r.Status == RentalOperationReceipt.StatusPending);
            if (pendingRefund != null && _operationExecutor != null && item != null)
            {
                await _operationExecutor.ExecuteRequestRefundAsync(order.Id, rentalItemId, refundOpKey, cancellationToken);
            }
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "RELEASE_REJECTED" : "RELEASE_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Release rejected by Inventory" : "Release rejected";
            receipt.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> HandleMarkAssetsHandedOverResultAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        JsonElement payloadElement,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            receipt.MarkSucceeded(utcNow, null, payloadElement.GetRawText());
            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            if (item != null)
            {
                foreach (var assignment in item.UnitAssignments)
                {
                    assignment.ConfirmHandoverCompleted();
                }

                item.ApplyHandover(utcNow, operationKey);
                order.EvaluateOrderStatusRollup(utcNow, operationKey);

            }
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "HANDOVER_REJECTED" : "HANDOVER_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Handover rejected by Inventory" : "Handover rejected";
            receipt.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
