#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
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
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _inventoryCommands = inventoryCommands ?? throw new ArgumentNullException(nameof(inventoryCommands));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierDecisionService>.Instance;
    }

    public async Task<Result<SupplierDecisionResponse>> AcceptItemAsync(
        Guid itemId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        var callerValidation = ValidateSupplierCaller(caller);
        if (callerValidation.IsFailure) return callerValidation.Error;

        Guid supplierId = caller.UserId!.Value;

        // Lookup only OrderId before lock using AsNoTracking
        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue)
        {
            return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");
        }

        return await _operationExecutor.ExecuteSupplierAcceptAsync(
            orderId.Value,
            itemId,
            supplierId,
            idempotencyKey.Trim(),
            cancellationToken);
    }

    public async Task<Result<SupplierDecisionResponse>> RejectItemAsync(
        Guid itemId,
        SupplierRejectRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        var callerValidation = ValidateSupplierCaller(caller);
        if (callerValidation.IsFailure) return callerValidation.Error;

        if (request == null || string.IsNullOrWhiteSpace(request.RejectReason) || request.RejectReason.Trim().Length > 500)
        {
            return Error.Validation("INVALID_REJECT_REASON", "Lý do từ chối là bắt buộc và tối đa 500 ký tự.");
        }

        if (request.OptionalNote != null && request.OptionalNote.Trim().Length > 2000)
        {
            return Error.Validation("INVALID_OPTIONAL_NOTE", "Ghi chú không được vượt quá 2000 ký tự.");
        }

        Guid supplierId = caller.UserId!.Value;

        // Lookup only OrderId before lock using AsNoTracking
        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue)
        {
            return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");
        }

        return await _operationExecutor.ExecuteSupplierRejectAsync(
            orderId.Value,
            itemId,
            supplierId,
            request.RejectReason.Trim(),
            string.IsNullOrWhiteSpace(request.OptionalNote) ? null : request.OptionalNote.Trim(),
            idempotencyKey.Trim(),
            cancellationToken);
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

        var callerValidation = ValidateSupplierCaller(caller);
        if (callerValidation.IsFailure) return callerValidation.Error;

        if (request == null || request.Units == null || request.Units.Count == 0)
        {
            return Error.Validation("INVALID_REQUEST", "Thông tin bàn giao không hợp lệ.");
        }

        if (request.ActualHandoverAt == default)
        {
            return Error.Validation("INVALID_HANDOVER_TIME", "Thời gian bàn giao thực tế không hợp lệ.");
        }

        Guid supplierId = caller.UserId!.Value;
        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        if (item.SupplierId != supplierId)
        {
            return Error.Forbidden("SUPPLIER_MISMATCH", "Chỉ chủ thiết bị mới có quyền bàn giao.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (request.ActualHandoverAt > utcNow.AddMinutes(5))
        {
            return Error.Validation("INVALID_HANDOVER_TIME", "Thời gian bàn giao thực tế không thể ở tương lai.");
        }

        if (request.Units.Count != item.UnitAssignments.Count ||
            request.Units.Select(u => u.AssignmentId).Distinct().Count() != request.Units.Count ||
            request.Units.Any(u => !item.UnitAssignments.Any(a => a.Id == u.AssignmentId)))
        {
            return Error.Validation("INVALID_UNITS", "Danh sách thiết bị bàn giao phải bao gồm đầy đủ và duy nhất các máy thuộc mục thuê.");
        }

        if (request.Units.Any(u => u.MediaIds == null || u.MediaIds.Count == 0))
        {
            return Error.Validation("MISSING_MEDIA", "Mỗi thiết bị bàn giao phải kèm bằng chứng hình ảnh.");
        }

        string opKey = idempotencyKey.Trim();
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = "MarkAssetsHandedOver",
            ItemId = item.Id,
            ActualHandoverAt = request.ActualHandoverAt,
            Units = request.Units.OrderBy(u => u.AssignmentId).Select(u => new
            {
                u.AssignmentId,
                u.Condition,
                u.Note,
                MediaIds = (u.MediaIds ?? []).OrderBy(m => m).ToList()
            }).ToList()
        });

        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
        if (existingReceipt != null)
        {
            if (existingReceipt.Action != "MarkAssetsHandedOver" ||
                existingReceipt.TargetItemId != item.Id ||
                !string.Equals(existingReceipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được dùng với payload khác.");
            }

            var existingRecord = item.HandoverRecord;
            return new HandoverItemResponse(existingRecord?.Id ?? Guid.Empty, request.ActualHandoverAt, existingReceipt.Status);
        }

        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return Error.Conflict("INVALID_STATUS", $"Mục thuê không ở trạng thái AwaitingHandover. Trạng thái hiện tại: {item.Status}.");
        }

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
            var assignment = item.UnitAssignments.First(a => a.Id == u.AssignmentId);
            assignment.RecordHandoverEvidence(handoverRecord.Id, u.Condition, u.Note, u.MediaIds);
        }

        var handoverCmd = new MarkAssetsHandedOverCommandPayload(
            order.Id,
            item.Id,
            opKey,
            request.ActualHandoverAt);

        var enqueueResult = await _inventoryCommands.EnqueueMarkAssetsHandedOverAsync(
            handoverCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
        if (enqueueResult.IsFailure)
        {
            return enqueueResult.Error;
        }

        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(),
            order.Id,
            opKey,
            "MarkAssetsHandedOver",
            fingerprint,
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

    private static Result ValidateSupplierCaller(IRentalCallerContext caller)
    {
        if (!caller.IsAuthenticated || caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu đăng nhập tài khoản cá nhân của supplier.");
        }
        return Result.Success();
    }
}
