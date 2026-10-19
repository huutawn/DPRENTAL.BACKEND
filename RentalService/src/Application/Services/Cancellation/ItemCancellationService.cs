#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Cancellation;

public sealed class ItemCancellationService : IItemCancellationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ItemCancellationService> _logger;

    public ItemCancellationService(
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null,
        ILogger<ItemCancellationService>? logger = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemCancellationService>.Instance;
    }

    public async Task<Result<CancelNotDeliveredResponse>> CancelByRenterAsync(
        Guid itemId,
        CancelNotDeliveredRequest? request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key bắt buộc và không quá 64 ký tự.");
        }

        string? reason = request?.Reason?.Trim();
        if (reason?.Length > 500)
        {
            return Error.Validation("INVALID_REASON", "Lý do hủy không được vượt quá 500 ký tự.");
        }

        Guid? orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue)
        {
            return RentalErrors.ItemNotFound;
        }

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(15), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null)
        {
            return RentalErrors.ItemNotFound;
        }

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        var auth = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        if (auth.IsFailure)
        {
            return auth.Error.Type == ErrorType.NotFound ? RentalErrors.ItemNotFound : auth.Error;
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = RentalItem.CancelNotDeliveredAction,
            ItemId = item.Id,
            Reason = reason
        });

        // 5. Kiểm tra receipt cùng key
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            if (existingReceipt.Fingerprint == fingerprint)
            {
                if (!string.IsNullOrEmpty(existingReceipt.ApiResponseJson))
                {
                    var cached = JsonSerializer.Deserialize<CancelNotDeliveredResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    if (cached != null) return cached;
                }
                return new CancelNotDeliveredResponse(item.Id, item.Status.ToString(), existingReceipt.CreatedAt, item.RentalAmount + item.DepositAmount);
            }
            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được dùng với nội dung khác.");
        }

        // 6. Dòng đã Cancelled từ trước với key khác -> trả kết quả cũ (lũy đẳng)
        if (item.Status == RentalItemStatus.Cancelled)
        {
            var cancelReceipt = order.OperationReceipts
                .Where(r => r.TargetItemId == item.Id && r.Action == RentalItem.CancelNotDeliveredAction && r.Status == RentalOperationReceipt.StatusSucceeded)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefault();

            DateTimeOffset cancelledAt = cancelReceipt?.CreatedAt ?? utcNow;
            if (cancelReceipt != null && !string.IsNullOrEmpty(cancelReceipt.ApiResponseJson))
            {
                var cached = JsonSerializer.Deserialize<CancelNotDeliveredResponse>(cancelReceipt.ApiResponseJson, JsonOptions);
                if (cached != null) return cached;
            }
            return new CancelNotDeliveredResponse(item.Id, RentalItemStatus.Cancelled.ToString(), cancelledAt, item.RentalAmount + item.DepositAmount);
        }

        // 7. Dòng không ở AWAITING_HANDOVER -> 409 Order.CancellationForbidden
        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        // 8. Chưa qua mốc quá hạn giao -> 409 CANCEL_TOO_EARLY
        if (!item.IsHandoverOverdue(utcNow))
        {
            return RentalErrors.CancelTooEarly;
        }

        // 9. Bằng chứng bàn giao đang được xử lý dở dang -> 409 HANDOVER_IN_PROGRESS
        bool handoverInProgress = order.OperationReceipts.Any(r =>
            r.TargetItemId == item.Id &&
            r.Action == "MarkAssetsHandedOver" &&
            r.Status is RentalOperationReceipt.StatusPending or RentalOperationReceipt.StatusProcessing);

        if (handoverInProgress)
        {
            return RentalErrors.HandoverInProgress;
        }

        // 10. Chuyển trạng thái dòng, rollup đơn hàng
        Guid actorId = caller.UserId!.Value;
        string cancelReason = string.IsNullOrWhiteSpace(reason) ? "Renter hủy do thiết bị không được bàn giao" : reason;
        var cancelResult = item.ApplyNotDeliveredCancellation(actorId, ActorRole.Renter, utcNow, cancelReason, normalizedKey);
        if (cancelResult.IsFailure)
        {
            return cancelResult.Error;
        }

        order.EvaluateOrderStatusRollup(utcNow, normalizedKey);

        // 11. Lên lịch nhả lịch và hoàn tiền 100%
        await _operationExecutor.ScheduleReleaseAndRefundAsync(order, item, actorId, cancelReason, utcNow, cancellationToken);

        decimal refundAmount = item.RentalAmount + item.DepositAmount;
        var response = new CancelNotDeliveredResponse(item.Id, RentalItemStatus.Cancelled.ToString(), utcNow, refundAmount);
        string responseJson = JsonSerializer.Serialize(response, JsonOptions);

        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            RentalItem.CancelNotDeliveredAction,
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            targetItemId: item.Id,
            apiResponseJson: responseJson);
        order.AddOperationReceipt(receipt);

        // 12. Phát event RentalItemCancelled qua outbox
        var notifyEnvelope = new KafkaMessageEnvelope<RentalItemCancelledPayload>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.RentalItemCancelled,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            order.Id.ToString("D"),
            utcNow,
            normalizedKey,
            null,
            new RentalItemCancelledPayload(order.Id, item.Id, actorId, ActorRole.Renter.ToString(), utcNow, cancelReason, refundAmount));

        var outbox = new RentalOutboxMessage(
            notifyEnvelope.MessageId,
            KafkaContractSpec.Topics.RentalEvents,
            order.Id.ToString("D"),
            KafkaContractSpec.EventTypes.RentalItemCancelled,
            JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
            utcNow);

        await _outboxRepository.AddAsync(outbox, cancellationToken);

        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return response;
    }

    public async Task<Result<CancelNotDeliveredResponse>> CancelBySystemAsync(
        Guid itemId,
        string reason,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_OPERATION_KEY", "Operation key bắt buộc và không quá 64 ký tự.");
        }

        Guid? orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue)
        {
            return RentalErrors.ItemNotFound;
        }

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(15), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null)
        {
            return RentalErrors.ItemNotFound;
        }

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = operationKey.Trim();
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = RentalItem.CancelNotDeliveredAction,
            ItemId = item.Id,
            Role = ActorRole.System.ToString()
        });

        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            if (existingReceipt.Fingerprint == fingerprint)
            {
                if (!string.IsNullOrEmpty(existingReceipt.ApiResponseJson))
                {
                    var cached = JsonSerializer.Deserialize<CancelNotDeliveredResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    if (cached != null) return cached;
                }
                return new CancelNotDeliveredResponse(item.Id, item.Status.ToString(), existingReceipt.CreatedAt, item.RentalAmount + item.DepositAmount);
            }
            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa thao tác đã được dùng với nội dung khác.");
        }

        if (item.Status == RentalItemStatus.Cancelled)
        {
            var cancelReceipt = order.OperationReceipts
                .Where(r => r.TargetItemId == item.Id && r.Action == RentalItem.CancelNotDeliveredAction && r.Status == RentalOperationReceipt.StatusSucceeded)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefault();

            DateTimeOffset cancelledAt = cancelReceipt?.CreatedAt ?? utcNow;
            if (cancelReceipt != null && !string.IsNullOrEmpty(cancelReceipt.ApiResponseJson))
            {
                var cached = JsonSerializer.Deserialize<CancelNotDeliveredResponse>(cancelReceipt.ApiResponseJson, JsonOptions);
                if (cached != null) return cached;
            }
            return new CancelNotDeliveredResponse(item.Id, RentalItemStatus.Cancelled.ToString(), cancelledAt, item.RentalAmount + item.DepositAmount);
        }

        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        bool handoverInProgress = order.OperationReceipts.Any(r =>
            r.TargetItemId == item.Id &&
            r.Action == "MarkAssetsHandedOver" &&
            r.Status is RentalOperationReceipt.StatusPending or RentalOperationReceipt.StatusProcessing);

        if (handoverInProgress)
        {
            return RentalErrors.HandoverInProgress;
        }

        Guid actorId = Guid.Empty;
        var cancelResult = item.ApplyNotDeliveredCancellation(actorId, ActorRole.System, utcNow, reason, normalizedKey);
        if (cancelResult.IsFailure)
        {
            return cancelResult.Error;
        }

        order.EvaluateOrderStatusRollup(utcNow, normalizedKey);

        await _operationExecutor.ScheduleReleaseAndRefundAsync(order, item, actorId, reason, utcNow, cancellationToken);

        decimal refundAmount = item.RentalAmount + item.DepositAmount;
        var response = new CancelNotDeliveredResponse(item.Id, RentalItemStatus.Cancelled.ToString(), utcNow, refundAmount);
        string responseJson = JsonSerializer.Serialize(response, JsonOptions);

        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            RentalItem.CancelNotDeliveredAction,
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            targetItemId: item.Id,
            apiResponseJson: responseJson);
        order.AddOperationReceipt(receipt);

        var notifyEnvelope = new KafkaMessageEnvelope<RentalItemCancelledPayload>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.RentalItemCancelled,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            order.Id.ToString("D"),
            utcNow,
            normalizedKey,
            null,
            new RentalItemCancelledPayload(order.Id, item.Id, actorId, ActorRole.System.ToString(), utcNow, reason, refundAmount));

        var outbox = new RentalOutboxMessage(
            notifyEnvelope.MessageId,
            KafkaContractSpec.Topics.RentalEvents,
            order.Id.ToString("D"),
            KafkaContractSpec.EventTypes.RentalItemCancelled,
            JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
            utcNow);

        await _outboxRepository.AddAsync(outbox, cancellationToken);

        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return response;
    }
}
