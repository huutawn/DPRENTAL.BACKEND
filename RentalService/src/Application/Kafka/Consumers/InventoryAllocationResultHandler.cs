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
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
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
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOrderLockManager _lockManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InventoryAllocationResultHandler> _logger;
    private readonly IRentalEventProducer? _rentalEvents;
    private readonly Clients.IPaymentRentalClient? _paymentClient;

    public InventoryAllocationResultHandler(
        IRentalOrderRepository orderRepository,
        IUnitOfWork unitOfWork,
        IOrderLockManager lockManager,
        TimeProvider? timeProvider = null,
        IRentalCheckoutOperationRepository? checkoutOperationRepo = null,
        IInventoryCommandProducer? inventoryCommands = null,
        ICheckoutOrderWriter? orderWriter = null,
        ILogger<InventoryAllocationResultHandler>? logger = null,
        IRentalEventProducer? rentalEvents = null,
        Clients.IPaymentRentalClient? paymentClient = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkoutOperationRepo = checkoutOperationRepo;
        _inventoryCommands = inventoryCommands;
        _orderWriter = orderWriter;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InventoryAllocationResultHandler>.Instance;
        _rentalEvents = rentalEvents;
        _paymentClient = paymentClient;
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
        Guid payloadOrderId = payloadElement.TryGetProperty("rentalOrderId", out var roProp) && roProp.TryGetGuid(out var ro) ? ro : Guid.Empty;
        if (payloadOrderId == Guid.Empty || payloadOrderId != orderId || rentalItemId == Guid.Empty || string.IsNullOrWhiteSpace(operationKey))
            return Error.Validation("INVENTORY_RESULT_IDENTITY_MISMATCH", "Inventory result identity does not match its Kafka aggregate.");

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
            if (order != null)
            {
                return await HandleRecoveryHoldResultAsync(order, rentalItemId, operationKey, isSuccess, payloadElement, utcNow, cancellationToken);
            }
            return await HandleCreateHoldResultAsync(orderId, rentalItemId, operationKey, isSuccess, payloadElement, utcNow, cancellationToken);
        }

        // For non-CreateHold commands, an existing order is required (except compensation release on failed/timed-out checkout)
        if (order == null)
        {
            if (string.Equals(commandType, KafkaContractSpec.CommandTypes.ReleaseAllocation, StringComparison.OrdinalIgnoreCase) &&
                _checkoutOperationRepo != null &&
                await _checkoutOperationRepo.GetByExpectedOrderIdAsync(orderId, cancellationToken) is
                { Status: CheckoutOperationStatus.Failed or CheckoutOperationStatus.TimedOut })
            {
                _logger.LogInformation("Release reply {OperationKey} for unfinished checkout order {OrderId} acknowledged", operationKey, orderId);
                return Result.Success();
            }

            return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{orderId}'.");
        }

        var receipt = order.OperationReceipts.FirstOrDefault(r =>
            r.TargetItemId == rentalItemId &&
            (string.Equals(r.OperationId, operationKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.InventoryOperationKey, operationKey, StringComparison.OrdinalIgnoreCase)));
        if (receipt == null)
        {
            _logger.LogWarning("Receipt for operationKey {OperationKey} not found on order {OrderId}", operationKey, orderId);
            return Result.Success();
        }
        if (!IsReceiptForCommand(receipt, commandType))
            return Error.Validation("INVENTORY_RESULT_ACTION_MISMATCH", "Inventory result action does not match the staged operation.");

        if (receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            bool sameSuccess = isSuccess && !string.IsNullOrWhiteSpace(receipt.ResponseJson) &&
                CanonicalJson.AreEquivalent(receipt.ResponseJson, payloadElement.GetRawText());
            return sameSuccess ? Result.Success() : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Operation already has a different terminal result.");
        }
        if (receipt.Status == RentalOperationReceipt.StatusFailed)
        {
            string incomingCode = payloadElement.TryGetProperty("errorCode", out var errorCodeElement) ? errorCodeElement.GetString() ?? "" : "";
            bool sameFailure = !isSuccess && string.Equals(receipt.ErrorCode, incomingCode, StringComparison.OrdinalIgnoreCase);
            return sameFailure ? Result.Success() : Error.Conflict("INVENTORY_RESULT_CONFLICT", "Operation already has a different terminal result.");
        }

        if (string.Equals(commandType, KafkaContractSpec.CommandTypes.MarkAssetsOverdue, StringComparison.OrdinalIgnoreCase))
        {
            return HandleOverdueResult(order, receipt, operationKey, isSuccess, payloadElement, utcNow);
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

    private static bool IsReceiptForCommand(RentalOperationReceipt receipt, string commandType) => commandType switch
    {
        KafkaContractSpec.CommandTypes.ConfirmAllocation => receipt.Action is "SupplierAccept" or KafkaContractSpec.CommandTypes.ConfirmAllocation,
        KafkaContractSpec.CommandTypes.CreateHold => receipt.Action is "RecoveryHold" or KafkaContractSpec.CommandTypes.CreateHold,
        _ => string.Equals(receipt.Action, commandType, StringComparison.OrdinalIgnoreCase)
    };

    private static Result HandleOverdueResult(RentalOrder order, RentalOperationReceipt receipt,
        string operationKey, bool isSuccess, JsonElement payload, DateTimeOffset now)
    {
        if (receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();
        if (receipt.InventoryOperationKey is not null && !string.Equals(receipt.InventoryOperationKey, operationKey, StringComparison.Ordinal))
            return Error.Conflict("INVENTORY_RESULT_CONFLICT", "Overdue result operation key differs from the staged command.");
        if (isSuccess)
        {
            string status = payload.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "" : "";
            if (!string.Equals(status, "PROTECTED", StringComparison.OrdinalIgnoreCase))
                return Error.Validation("INVALID_OVERDUE_RESULT", "Inventory did not confirm overdue protection.");
            var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
            if (item is null || !payload.TryGetProperty("allocations", out var maps) || maps.ValueKind != JsonValueKind.Array)
                return Error.Validation("INVALID_OVERDUE_RESULT", "Overdue result has no allocation mapping.");
            OverdueProtectionCommand? command;
            try { command = JsonSerializer.Deserialize<OverdueProtectionCommand>(receipt.PayloadJson ?? "", JsonOptions); }
            catch (JsonException) { return Error.Validation("INVALID_OVERDUE_COMMAND", "Stored overdue command is invalid."); }
            var expected = command?.ProviderRequest?.AllocationIds?.Order().ToArray() ?? [];
            var actual = maps.EnumerateArray().Select(m => m.TryGetProperty("allocationId", out var id) && id.TryGetGuid(out var value) ? value : Guid.Empty).Order().ToArray();
            if (expected.Length != 1 || !expected.SequenceEqual(actual))
                return Error.Validation("INVALID_OVERDUE_RESULT", "Overdue result allocation does not match the pending assignment.");
            receipt.MarkSucceeded(now, payload.GetRawText());
            return Result.Success();
        }

        string errorCode = payload.TryGetProperty("errorCode", out var codeElement) ? codeElement.GetString() ?? "" : "";
        if (string.Equals(errorCode, "ALLOCATION_NOT_OVERDUE", StringComparison.OrdinalIgnoreCase) &&
            order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId)?.UnitAssignments
                .Any(u => u.Status is RentalUnitAssignmentStatus.ReturnedNormal or RentalUnitAssignmentStatus.ReturnedDamaged) == true)
        {
            receipt.MarkSucceeded(now);
            return Result.Success();
        }
        receipt.MarkFailed(errorCode, payload.TryGetProperty("errorMessage", out var errorElement) ? errorElement.GetString() : null,
            now, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        return Result.Success();
    }

    private async Task<Result> StageCheckoutReleasesAsync(Guid orderId, IEnumerable<Guid> rentalItemIds, CancellationToken ct)
    {
        foreach (Guid itemId in rentalItemIds.Distinct().Order())
        {
            var release = new ReleaseAllocationCommandPayload(orderId, itemId,
                $"checkout:compensation:{orderId:N}:{itemId:N}:release", "Checkout did not complete");
            var result = await _inventoryCommands!.EnqueueReleaseAllocationAsync(
                release, orderId.ToString("D"), cancellationToken: ct);
            if (result.IsFailure) return result.Error;
        }
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
            return Error.Failure("CHECKOUT_HANDLER_NOT_CONFIGURED",
                "Checkout allocation result handling requires its repository, Inventory producer and order writer.");
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
                var staged = await StageCheckoutReleasesAsync(orderId, [rentalItemId], cancellationToken);
                if (staged.IsFailure) return staged.Error;
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
                var staged = await StageCheckoutReleasesAsync(orderId, lines.Select(l => l.RentalItemId), cancellationToken);
                if (staged.IsFailure) return staged.Error;
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
                var staged = await StageCheckoutReleasesAsync(orderId, lines.Select(l => l.RentalItemId), cancellationToken);
                if (staged.IsFailure) return staged.Error;
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var linesMap = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions) ?? new();
        var targetLine = linesMap.FirstOrDefault(l => l.RentalItemId == rentalItemId);
        if (targetLine == null)
        {
            _logger.LogWarning("Line for rentalItemId {RentalItemId} not found in operation {OperationId}", rentalItemId, op.OperationId);
            op.MarkFailed("CHECKOUT_LINE_NOT_FOUND", "The successful hold result does not match a checkout line.", utcNow);
            var staged = await StageCheckoutReleasesAsync(orderId, linesMap.Select(l => l.RentalItemId), cancellationToken);
            if (staged.IsFailure) return staged.Error;
            await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var plan = JsonSerializer.Deserialize<CheckoutPlan>(op.PreparationSnapshotJson, JsonOptions);
        if (plan is null)
        {
            op.MarkFailed("CHECKOUT_SNAPSHOT_INVALID", "Checkout snapshot is invalid.", utcNow);
            var staged = await StageCheckoutReleasesAsync(orderId, linesMap.Select(l => l.RentalItemId), cancellationToken);
            if (staged.IsFailure) return staged.Error;
            await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        var planLine = plan.Lines.FirstOrDefault(pl => pl.CartItemId == targetLine.CartItemId);
        if (planLine is null)
        {
            op.MarkFailed("CHECKOUT_PLAN_LINE_MISSING", "Checkout snapshot is missing the held cart line.", utcNow);
            var staged = await StageCheckoutReleasesAsync(orderId, linesMap.Select(l => l.RentalItemId), cancellationToken);
            if (staged.IsFailure) return staged.Error;
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        int expectedQuantity = planLine.Quantity;

        // Validate allocations from Inventory
        if (!payloadElement.TryGetProperty("allocations", out var alProp) || alProp.ValueKind != JsonValueKind.Array)
        {
            op.MarkFailed("INVALID_ALLOCATIONS_PAYLOAD", "Missing allocations array from Inventory", utcNow);
            var staged = await StageCheckoutReleasesAsync(orderId, linesMap.Select(l => l.RentalItemId), cancellationToken);
            if (staged.IsFailure) return staged.Error;
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var allocs = JsonSerializer.Deserialize<List<AllocationAssetMappingPayload>>(alProp.GetRawText(), JsonOptions);
        if (allocs == null || allocs.Count != expectedQuantity ||
            allocs.Select(a => a.AllocationId).Distinct().Count() != allocs.Count ||
            allocs.Select(a => a.AssetId).Distinct().Count() != allocs.Count ||
            allocs.Any(a => a.AllocationId == Guid.Empty || a.AssetId == Guid.Empty))
        {
            op.MarkFailed("ALLOCATIONS_VALIDATION_FAILED", "Allocations quantity mismatch or duplicate IDs from Inventory", utcNow);
            var staged = await StageCheckoutReleasesAsync(orderId, linesMap.Select(l => l.RentalItemId), cancellationToken);
            if (staged.IsFailure) return staged.Error;
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
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
                var staged = await StageCheckoutReleasesAsync(orderId, linesMap.Select(l => l.RentalItemId), cancellationToken);
                if (staged.IsFailure) return staged.Error;
                await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }

            var createdOrder = orderResult.Value;
            if (_rentalEvents is not null)
            {
                var orderCreated = await _rentalEvents.EnqueueRentalOrderCreatedAsync(
                    new RentalOrderCreatedPayload(
                        createdOrder.Id,
                        createdOrder.OrderCode,
                        createdOrder.RenterId,
                        createdOrder.Currency,
                        createdOrder.TotalRentalAmount,
                        createdOrder.TotalDepositAmount,
                        createdOrder.TotalPayableAmount,
                        createdOrder.PaymentDeadline,
                        createdOrder.CreatedAt,
                        createdOrder.Items.Select(item => new RentalOrderCreatedItemPayload(
                            item.Id,
                            item.EquipmentListingId,
                            item.SupplierId,
                            item.Quantity,
                            item.ExpectedStartDate,
                            item.ExpectedEndDate,
                            item.RentalAmount,
                            item.DepositAmount)).ToArray()),
                    correlationId: orderId.ToString("D"),
                    cancellationToken: cancellationToken);
                if (orderCreated.IsFailure) return orderCreated.Error;
            }
            else
            {
                return Error.Failure("RENTAL_EVENT_PRODUCER_NOT_CONFIGURED",
                    "RentalOrderCreated must be staged before checkout acknowledgement.");
            }
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
            string? apiResponseJson = null;
            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            if (item != null)
            {
                // Invariant: Do not restore Rejected or Expired items!
                if (item.Status == RentalItemStatus.PendingSupplierConfirmation)
                {
                    item.ApplySupplierAccept(item.SupplierId, utcNow, operationKey, operationKey);
                    RentalOrderStatusPolicy.Apply(order, utcNow, operationKey);
                    order.RecordReadModelUpdate(utcNow);
                    apiResponseJson = RentalOperationExecutor.SerializeSupplierResponse(order, item);
                    var outboxPayload = new RentalItemConfirmedPayload(
                        order.Id,
                        item.Id,
                        item.SupplierId,
                        utcNow,
                        operationKey);

                    var published = await _rentalEvents!.EnqueueDomainEventAsync(
                        KafkaContractSpec.EventTypes.RentalItemConfirmed, item.Id.ToString("D"),
                        order.Id.ToString("D"), utcNow, outboxPayload,
                        order.Id.ToString("D"), causationId: receipt.Id, cancellationToken: cancellationToken);
                    if (published.IsFailure) return published.Error;
                }
                else
                {
                    _logger.LogWarning("ConfirmAllocation succeeded late for item {ItemId} in status {Status}; not restoring", item.Id, item.Status);
                }

                receipt.MarkSucceeded(utcNow, payloadElement.GetRawText(), apiResponseJson);
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
            receipt.MarkSucceeded(utcNow, payloadElement.GetRawText());
            await CheckAndCompleteHoldConfirmationAsync(order, utcNow, cancellationToken);
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "HOLD_EXTENSION_REJECTED" : "HOLD_EXTENSION_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Hold extension rejected" : "Rejected";
            receipt.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);

            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            if (item != null)
            {
                DateTimeOffset cutoff = CalculateRecoveryCutoff(order);
                string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{order.Id}:{item.Id}");
                bool alreadyAttempted = order.OperationReceipts.Any(r => r.OperationId == recOpKey);

                if (string.Equals(errCode, "HOLD_EXPIRED", StringComparison.OrdinalIgnoreCase) &&
                    order.Status != RentalOrderStatus.Cancelled &&
                    utcNow < cutoff &&
                    !alreadyAttempted)
                {
                    _logger.LogInformation("ExtendHold expired for item {ItemId} on order {OrderId}. Attempting recovery hold re-allocation before cutoff {Cutoff}.", item.Id, order.Id, cutoff);

                    string recGroupId = $"rental-item:{item.Id}:rec-1";
                    DateTimeOffset holdExpiry = item.SupplierConfirmationDeadline is DateTimeOffset deadline
                        ? RentalItem.CalculateConfirmationHoldExpiry(deadline, item.ExpectedStartDate)
                        : cutoff;

                    var createHoldReq = new CreateHoldCommandPayload(
                        order.Id,
                        item.Id,
                        recOpKey,
                        item.EquipmentListingId,
                        item.Quantity,
                        item.ExpectedStartDate,
                        item.ExpectedEndDate,
                        holdExpiry,
                        Notes: "Recovery hold after ExtendHold expired",
                        AllocationGroupId: recGroupId,
                        ExecutionCutoffUtc: cutoff);

                    var recReceipt = RentalOperationReceipt.CreatePending(
                        Guid.NewGuid(),
                        order.Id,
                        recOpKey,
                        "RecoveryHold",
                        RentalOperationExecutor.ComputeFingerprint($"RecoveryHold:{recGroupId}"),
                        utcNow,
                        item.Id,
                        JsonSerializer.Serialize(createHoldReq, JsonOptions));

                    order.AddOperationReceipt(recReceipt);

                    if (_inventoryCommands != null)
                    {
                        await _inventoryCommands.EnqueueCreateHoldAsync(createHoldReq, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                    }
                }
                else
                {
                    _logger.LogWarning("ExtendHold failed for item {ItemId} and cannot recover. Triggering all-or-nothing compensation for order {OrderId}.", item.Id, order.Id);
                    await CompensateAndRefundAllItemsAsync(order, errCode, errMsg, utcNow, cancellationToken);
                }
            }
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> HandleRecoveryHoldResultAsync(
        RentalOrder order,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        JsonElement payloadElement,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
        if (item == null)
        {
            _logger.LogWarning("Item {ItemId} not found on order {OrderId} for recovery hold reply", rentalItemId, order.Id);
            return Result.Success();
        }

        if (order.Status == RentalOrderStatus.Cancelled)
        {
            _logger.LogWarning("Recovery hold reply arrived for cancelled order {OrderId}. Releasing allocation.", order.Id);
            string relGroup = payloadElement.TryGetProperty("allocationGroupId", out var ag) ? ag.GetString() ?? item.AllocationGroupId : item.AllocationGroupId;
            if (_inventoryCommands != null)
            {
                await _inventoryCommands.EnqueueReleaseAllocationAsync(
                    new ReleaseAllocationCommandPayload(order.Id, item.Id, operationKey + ":rel", "Order was cancelled", relGroup),
                    order.Id.ToString("D"), cancellationToken: cancellationToken);
            }
            return Result.Success();
        }

        string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{order.Id}:{item.Id}");
        var receipt = order.OperationReceipts.FirstOrDefault(r =>
            r.TargetItemId == rentalItemId &&
            (string.Equals(r.OperationId, operationKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.OperationId, recOpKey, StringComparison.OrdinalIgnoreCase)));

        if (isSuccess)
        {
            receipt?.MarkSucceeded(utcNow, payloadElement.GetRawText());

            string allocatedGroup = payloadElement.TryGetProperty("allocationGroupId", out var ag)
                ? ag.GetString() ?? $"rental-item:{rentalItemId}:rec-1"
                : $"rental-item:{rentalItemId}:rec-1";

            var assignments = new List<InitialRentalUnitAssignmentDto>();
            if (payloadElement.TryGetProperty("allocations", out var allocsArr) && allocsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in allocsArr.EnumerateArray())
                {
                    Guid allocId = el.GetProperty("allocationId").GetGuid();
                    Guid assetId = el.GetProperty("assetId").GetGuid();
                    assignments.Add(new InitialRentalUnitAssignmentDto(Guid.NewGuid(), assetId, allocId));
                }
            }

            if (assignments.Count > 0)
            {
                item.ReassignAllocationGroup(allocatedGroup, assignments);
            }

            await CheckAndCompleteHoldConfirmationAsync(order, utcNow, cancellationToken);
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "RECOVERY_HOLD_REJECTED" : "RECOVERY_HOLD_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Recovery hold rejected" : "Rejected";
            receipt?.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);

            _logger.LogWarning("Recovery hold rejected for item {ItemId} on order {OrderId} ({ErrCode}: {ErrMsg}). Triggering all-or-nothing compensation.",
                item.Id, order.Id, errCode, errMsg);

            await CompensateAndRefundAllItemsAsync(order, errCode, errMsg, utcNow, cancellationToken);
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static DateTimeOffset CalculateRecoveryCutoff(RentalOrder order)
    {
        DateTimeOffset paymentCutoff = order.PaymentDeadline.AddMinutes(15);
        DateTimeOffset supplierCutoff = order.Items
            .Where(i => i.SupplierConfirmationDeadline.HasValue)
            .Select(i => i.SupplierConfirmationDeadline!.Value)
            .DefaultIfEmpty(paymentCutoff)
            .Min();
        DateTimeOffset startOfDayCutoff = order.Items
            .Select(i => Clients.RentalInventoryDateAdapter.GetStartOfDayUtc(i.ExpectedStartDate))
            .Min();

        DateTimeOffset cutoff = paymentCutoff < supplierCutoff ? paymentCutoff : supplierCutoff;
        return cutoff < startOfDayCutoff ? cutoff : startOfDayCutoff;
    }

    private async Task<Result> CheckAndCompleteHoldConfirmationAsync(
        RentalOrder order,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        foreach (var item in order.Items)
        {
            string extendOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
            string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{order.Id}:{item.Id}");

            var extendReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == extendOpKey && r.Status == RentalOperationReceipt.StatusSucceeded);
            var recReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == recOpKey && r.Status == RentalOperationReceipt.StatusSucceeded);

            if (extendReceipt == null && recReceipt == null)
            {
                return Result.Success();
            }
        }

        bool usedRecovery = order.Items.Any(item =>
        {
            string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{order.Id}:{item.Id}");
            return order.OperationReceipts.Any(r => r.OperationId == recOpKey && r.Status == RentalOperationReceipt.StatusSucceeded);
        });

        bool wasLatePayment = order.LatePaymentRefundRequired ||
                              order.Status == RentalOrderStatus.Expired ||
                              usedRecovery ||
                              (order.PaymentConfirmedAt.HasValue && order.PaymentConfirmedAt.Value >= order.PaymentDeadline);

        if (wasLatePayment && !string.IsNullOrWhiteSpace(order.InvoiceRef) && !string.IsNullOrWhiteSpace(order.PaymentRef) && _paymentClient != null)
        {
            string acceptOpKey = RentalOperationExecutor.ComputeOperationKey("AcceptLatePayment", order.Id.ToString());
            string recoveryId = $"rec-{order.Id:N}";
            var acceptReq = new AcceptLatePaymentRequest(acceptOpKey, order.Id, order.PaymentRef, recoveryId);

            var acceptRes = await _paymentClient.AcceptLatePaymentAsync(order.InvoiceRef, acceptReq, cancellationToken);
            if (acceptRes.IsFailure)
            {
                _logger.LogWarning("PaymentService rejected accept-late-payment for Order {OrderId}: {Error}. Triggering all-or-nothing compensation.", order.Id, acceptRes.Error.Code);
                return await CompensateAndRefundAllItemsAsync(order, acceptRes.Error.Code, acceptRes.Error.Message, utcNow, cancellationToken);
            }
        }

        order.ClearLatePaymentFlag();
        var markResult = order.MarkAsPaid(order.InvoiceRef ?? string.Empty, order.PaymentRef ?? string.Empty, order.PaymentConfirmedAt ?? utcNow, utcNow);
        if (markResult.IsFailure) return markResult.Error;

        if (_rentalEvents != null)
        {
            await _rentalEvents.EnqueueRentalOrderPaidAsync(
                new RentalOrderPaidPayload(order.Id, order.RenterId, order.InvoiceRef ?? string.Empty,
                    order.PaymentRef ?? string.Empty, order.TotalPayableAmount, order.PaymentConfirmedAt ?? utcNow),
                correlationId: order.Id.ToString("D"),
                cancellationToken: cancellationToken);

            foreach (var item in order.Items)
            {
                if (item.EquipmentTypeId.HasValue && item.EquipmentTypeId.Value != Guid.Empty)
                {
                    await _rentalEvents.EnqueueRentalItemPaidAsync(
                        new RentalItemPaidPayload(order.RenterId, item.EquipmentListingId,
                            item.EquipmentTypeId, order.Id, item.Id),
                        correlationId: order.Id.ToString("D"),
                        cancellationToken: cancellationToken);
                }

                await _rentalEvents.EnqueueDomainEventAsync(
                    KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
                    item.Id.ToString("D"), order.Id.ToString("D"), utcNow,
                    new RentalSupplierConfirmationRequestedPayload(
                        order.Id,
                        item.Id,
                        item.SupplierId,
                        item.EquipmentListingId,
                        item.Quantity,
                        item.SupplierConfirmationDeadline ?? utcNow.AddHours(24)),
                    order.Id.ToString("D"), null, cancellationToken);
            }
        }

        return Result.Success();
    }

    private async Task<Result> CompensateAndRefundAllItemsAsync(
        RentalOrder order,
        string errorCode,
        string errorMessage,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        order.MarkAsLatePayment(order.InvoiceRef ?? string.Empty, order.PaymentRef ?? string.Empty, order.PaymentConfirmedAt ?? utcNow, utcNow);

        foreach (var item in order.Items)
        {
            if (item.Status != RentalItemStatus.Expired && item.Status != RentalItemStatus.Cancelled)
            {
                item.ApplyHoldExpired(utcNow, order.PaymentRef ?? string.Empty, errorMessage);
            }
            string initialGroup = $"rental-item:{item.Id}";
            string releaseKey1 = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}:init");
            var existingRel1 = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseKey1);
            if (existingRel1 == null)
            {
                var relCmd1 = new ReleaseAllocationCommandPayload(order.Id, item.Id, releaseKey1, errorMessage, initialGroup);
                var receipt1 = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), order.Id, releaseKey1, "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{initialGroup}"),
                    utcNow, item.Id, JsonSerializer.Serialize(relCmd1, JsonOptions));
                receipt1.SetInventoryOperationKey(releaseKey1);
                order.AddOperationReceipt(receipt1);
                if (_inventoryCommands != null)
                {
                    await _inventoryCommands.EnqueueReleaseAllocationAsync(relCmd1, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                }
            }

            if (!string.Equals(item.AllocationGroupId, initialGroup, StringComparison.OrdinalIgnoreCase))
            {
                string releaseKey2 = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}:rec");
                var existingRel2 = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseKey2);
                if (existingRel2 == null)
                {
                    var relCmd2 = new ReleaseAllocationCommandPayload(order.Id, item.Id, releaseKey2, errorMessage, item.AllocationGroupId);
                    var receipt2 = RentalOperationReceipt.CreatePending(
                        Guid.NewGuid(), order.Id, releaseKey2, "ReleaseAllocation",
                        RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{item.AllocationGroupId}"),
                        utcNow, item.Id, JsonSerializer.Serialize(relCmd2, JsonOptions));
                    receipt2.SetInventoryOperationKey(releaseKey2);
                    order.AddOperationReceipt(receipt2);
                    if (_inventoryCommands != null)
                    {
                        await _inventoryCommands.EnqueueReleaseAllocationAsync(relCmd2, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                    }
                }
            }

            string refundKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
            var existingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundKey);
            if (existingRefund == null && !string.IsNullOrWhiteSpace(order.InvoiceRef) && !string.IsNullOrWhiteSpace(order.PaymentRef))
            {
                var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                var refundReq = new RequestRefundRequest(order.Id, order.InvoiceRef, order.PaymentRef, refundItems, errorMessage, refundKey);
                var refundReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(), order.Id, refundKey, "RequestRefund",
                    RentalOperationExecutor.ComputeFingerprint($"RequestRefund:{order.Id}:{item.TotalAmount}"),
                    isSuccess: false, errorCode: null, errorMessage: null,
                    createdAt: utcNow, status: RentalOperationReceipt.StatusPending,
                    targetItemId: item.Id, payloadJson: JsonSerializer.Serialize(refundReq, JsonOptions));
                order.AddOperationReceipt(refundReceipt);
            }
        }

        RentalOrderStatusPolicy.Apply(order, utcNow, "Compensation");

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
            receipt.MarkSucceeded(utcNow, payloadElement.GetRawText());
            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            bool assignmentsChanged = item?.MarkAssignmentsReleased(utcNow) ?? false;
            bool rollupChanged = RentalOrderStatusPolicy.Apply(order, utcNow, receipt.OperationId);

            if (assignmentsChanged || rollupChanged)
            {
                order.RecordReadModelUpdate(utcNow);
            }
            string refundOpKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{rentalItemId}");
            var pendingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey && r.Status == RentalOperationReceipt.StatusPending);
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
        // Kết quả trùng lặp cho một lệnh đã xử lý xong thì bỏ qua.
        if (receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return Result.Success();
        }

        if (isSuccess)
        {
            var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
            if (item != null)
            {
                if (item.HandoverRecord is not null)
                {
                    // Receipt tạo trước khi bằng chứng được hoãn tới lúc Inventory xác nhận: bản ghi đã có sẵn.
                    foreach (var assignment in item.UnitAssignments)
                    {
                        assignment.ConfirmHandoverCompleted();
                    }

                    item.ApplyHandover(utcNow, operationKey);
                }
                else
                {
                    var snapshot = HandoverEvidenceSnapshot.TryParse(receipt.PayloadJson);
                    if (snapshot is null)
                    {
                        _logger.LogError("Handover receipt {OperationKey} of item {ItemId} has no evidence payload", operationKey, rentalItemId);
                        receipt.MarkManualReconciliationRequired(utcNow, "Inventory đã xác nhận bàn giao nhưng receipt không có bằng chứng để ghi nhận.");
                        await _orderRepository.UpdateAsync(order, cancellationToken);
                        await _unitOfWork.SaveChangesAsync(cancellationToken);
                        return Result.Success();
                    }

                    var completed = item.CompleteHandover(
                        snapshot.SubmittedById,
                        snapshot.SubmittedAt,
                        snapshot.ActualHandoverAt,
                        snapshot.Note,
                        snapshot.ToUnitData(),
                        utcNow,
                        operationKey);
                    if (completed.IsFailure)
                    {
                        _logger.LogWarning("Handover result {OperationKey} not applied to item {ItemId}: {Code}", operationKey, rentalItemId, completed.Error.Code);
                        receipt.MarkManualReconciliationRequired(utcNow, $"Không ghi nhận được bàn giao: {completed.Error.Code}.");
                        await _orderRepository.UpdateAsync(order, cancellationToken);
                        await _unitOfWork.SaveChangesAsync(cancellationToken);
                        return Result.Success();
                    }
                }

                RentalOrderStatusPolicy.Apply(order, utcNow, operationKey);
                order.RecordReadModelUpdate(utcNow);
            }

            receipt.MarkSucceeded(utcNow, payloadElement.GetRawText());
        }
        else
        {
            string errCode = payloadElement.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "HANDOVER_REJECTED" : "HANDOVER_REJECTED";
            string errMsg = payloadElement.TryGetProperty("errorMessage", out var em) ? em.GetString() ?? "Handover rejected by Inventory" : "Handover rejected";
            receipt.MarkFailed(errCode, errMsg, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
        }

        await _orderRepository.UpdateAsync(order, cancellationToken);

        var handedOverItem = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
        if (isSuccess && handedOverItem?.HandoverRecord is { } handover)
        {
            var handoverPayload = new RentalItemHandoverCompletedPayload(
                order.Id, handedOverItem.Id, order.RenterId, handedOverItem.SupplierId,
                handedOverItem.EquipmentListingId, handedOverItem.Quantity, handover.Id,
                handover.ActualHandoverAt, utcNow);
            var published = await _rentalEvents!.EnqueueDomainEventAsync(
                KafkaContractSpec.EventTypes.RentalItemHandoverCompleted, handover.Id.ToString("D"),
                order.Id.ToString("D"), utcNow, handoverPayload,
                order.Id.ToString("D"), receipt.Id, cancellationToken);
            if (published.IsFailure) return published.Error;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
