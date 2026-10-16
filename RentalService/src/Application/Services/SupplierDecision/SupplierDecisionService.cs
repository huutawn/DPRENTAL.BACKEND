#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.SupplierDecision;

using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

public sealed class SupplierDecisionService : ISupplierDecisionService
{
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IOrderLockManager _lockManager;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SupplierDecisionService> _logger;

    public SupplierDecisionService(
        IRentalOrderRepository orderRepository,
        IRentalOperationExecutor operationExecutor,
        IOrderLockManager lockManager,
        IInventoryCommandProducer inventoryCommands,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null,
        ILogger<SupplierDecisionService>? logger = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _lockManager = lockManager!;
        _inventoryCommands = inventoryCommands!;
        _unitOfWork = unitOfWork!;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierDecisionService>.Instance;
    }

    public SupplierDecisionService(
        IRentalOrderRepository orderRepository,
        IRentalOperationExecutor operationExecutor,
        ILogger<SupplierDecisionService>? logger = null)
        : this(orderRepository, operationExecutor, null!, null!, null!, null, logger)
    {
    }

    public async Task<Result<SubOrderDecisionResponse>> AcceptSubOrderAsync(
        Guid subOrderId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        if (supplier.IsFailure) return supplier.Error;

        Guid supplierId = supplier.Value;

        // Lookup only the sub-order before lock using AsNoTracking
        Result<RentalSubOrder> owned = RentalAuthorizationPolicy.AuthorizeSupplierSubOrder(
            supplierId, await _orderRepository.GetSubOrderByIdAsync(subOrderId, cancellationToken));
        if (owned.IsFailure) return owned.Error;
        RentalSubOrder subOrder = owned.Value;

        var result = await _operationExecutor.ExecuteSubOrderAcceptAsync(
            subOrder.RentalOrderId,
            subOrder.Id,
            supplierId,
            idempotencyKey.Trim(),
            cancellationToken);

        return result.IsSuccess ? ToResponse(subOrder, result.Value) : result.Error;
    }

    public async Task<Result<SubOrderDecisionResponse>> RejectSubOrderAsync(
        Guid subOrderId,
        SupplierRejectRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        if (supplier.IsFailure) return supplier.Error;

        if (request == null || string.IsNullOrWhiteSpace(request.RejectReason) || request.RejectReason.Trim().Length > 500)
        {
            return Error.Validation("INVALID_REJECT_REASON", "Lý do từ chối là bắt buộc và tối đa 500 ký tự.");
        }

        if (request.OptionalNote != null && request.OptionalNote.Trim().Length > 2000)
        {
            return Error.Validation("INVALID_OPTIONAL_NOTE", "Ghi chú không được vượt quá 2000 ký tự.");
        }

        Guid supplierId = supplier.Value;

        // Lookup only the sub-order before lock using AsNoTracking
        Result<RentalSubOrder> owned = RentalAuthorizationPolicy.AuthorizeSupplierSubOrder(
            supplierId, await _orderRepository.GetSubOrderByIdAsync(subOrderId, cancellationToken));
        if (owned.IsFailure) return owned.Error;
        RentalSubOrder subOrder = owned.Value;

        var result = await _operationExecutor.ExecuteSubOrderRejectAsync(
            subOrder.RentalOrderId,
            subOrder.Id,
            supplierId,
            request.RejectReason.Trim(),
            string.IsNullOrWhiteSpace(request.OptionalNote) ? null : request.OptionalNote.Trim(),
            idempotencyKey.Trim(),
            cancellationToken);

        return result.IsSuccess ? ToResponse(subOrder, result.Value) : result.Error;
    }

    private static SubOrderDecisionResponse ToResponse(RentalSubOrder subOrder, IReadOnlyList<SupplierDecisionResponse> items)
    {
        RentalItemStatus status = SubOrderStatusPolicy.Summarize(
            items.Select(i => Enum.Parse<RentalItemStatus>(i.Status)).ToList());
        return new SubOrderDecisionResponse(subOrder.Id, subOrder.SubOrderCode, subOrder.RentalOrderId, status.ToString(), items);
    }

    public async Task<Result<HandoverItemResponse>> HandoverItemAsync(
        Guid itemId,
        HandoverItemRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        if (supplier.IsFailure) return supplier.Error;

        if (request == null || request.Units == null || request.Units.Count == 0)
        {
            return Error.Validation("INVALID_REQUEST", "Thông tin bàn giao không hợp lệ.");
        }

        Guid supplierId = supplier.Value;
        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order == null) return RentalErrors.ItemNotFound;

        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return Error.Conflict("INVALID_STATUS", $"Mục thuê không ở trạng thái AwaitingHandover. Trạng thái hiện tại: {item.Status}.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var handoverRecord = new HandoverRecord(
            Guid.NewGuid(),
            item.Id,
            request.ActualHandoverAt,
            request.Note,
            supplierId,
            utcNow,
            utcNow);

        item.SetHandoverRecord(handoverRecord);

        foreach (var u in request.Units)
        {
            var assignment = item.UnitAssignments.FirstOrDefault(a => a.Id == u.AssignmentId);
            if (assignment != null)
            {
                assignment.RecordHandover(handoverRecord.Id, u.Condition, u.Note, u.MediaIds);
            }
        }

        string opKey = idempotencyKey.Trim();

        var handoverCmd = new MarkAssetsHandedOverCommandPayload(
            order.Id,
            item.Id,
            opKey,
            request.ActualHandoverAt);

        await _inventoryCommands.EnqueueMarkAssetsHandedOverAsync(handoverCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);

        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(),
            order.Id,
            opKey,
            "MarkAssetsHandedOver",
            RentalOperationExecutor.ComputeFingerprint($"Handover:{item.Id}:{handoverRecord.Id}"),
            utcNow,
            item.Id,
            JsonSerializer.Serialize(handoverCmd));

        order.AddOperationReceipt(receipt);
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.CommitAsync(cancellationToken);

        return new HandoverItemResponse(handoverRecord.Id, request.ActualHandoverAt, "Processing");
    }

    private static Result ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và không được vượt quá 64 ký tự.");
        }
        return Result.Success();
    }
}
