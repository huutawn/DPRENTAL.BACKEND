#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor : IRentalOperationExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOperationReceiptRepository _receiptRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IInventoryRentalClient _inventoryClient;
    private readonly IPaymentRentalClient _paymentClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalOperationExecutor> _logger;
    private readonly string _rentalEventsTopic;

    public RentalOperationExecutor(
        IRentalOrderRepository orderRepository,
        IOperationReceiptRepository receiptRepository,
        IOutboxRepository outboxRepository,
        IOrderLockManager lockManager,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<RentalOperationExecutor> logger,
        TimeProvider? timeProvider = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _receiptRepository = receiptRepository ?? throw new ArgumentNullException(nameof(receiptRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _inventoryClient = inventoryClient ?? throw new ArgumentNullException(nameof(inventoryClient));
        _paymentClient = paymentClient ?? throw new ArgumentNullException(nameof(paymentClient));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
    }

    public static string ComputeOperationKey(string action, string logicalIdentity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{action}:{logicalIdentity}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    public static string ComputeFingerprint<T>(T value)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[] hash = SHA256.HashData(jsonBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFingerprint(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    public async Task<Result> ExecuteScheduledOperationAsync(
        OperationReceiptDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (descriptor.RetryCount >= 10)
        {
            return Result.Success();
        }

        if (descriptor.NextRetryAt.HasValue && descriptor.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(descriptor.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(descriptor.RentalOrderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == descriptor.OperationId);
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();
        if (receipt.Status == RentalOperationReceipt.StatusFailed &&
            receipt.ErrorClassification is RentalOperationReceipt.ClassificationBusiness or RentalOperationReceipt.ClassificationUncertainExhausted or RentalOperationReceipt.ClassificationTransientExhausted)
        {
            return Result.Success();
        }

        if (receipt.NextRetryAt.HasValue && receipt.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        // Prerequisite check: must be confirmed Succeeded before executing
        if (!string.IsNullOrWhiteSpace(receipt.PrerequisiteOperationId))
        {
            var prereq = order.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.PrerequisiteOperationId);
            if (prereq == null || !prereq.IsSuccess || prereq.Status != RentalOperationReceipt.StatusSucceeded)
            {
                _logger.LogInformation("Scheduled operation {OpId} waiting for prerequisite {Prereq}", receipt.OperationId, receipt.PrerequisiteOperationId);
                return Result.Success();
            }
        }

        switch (receipt.Action)
        {
            case RentalItem.BlockDamagedAction:
            case RentalItem.MarkReturnedAction:
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item is null) return Error.NotFound("ITEM_NOT_FOUND", "Rental item was not found.");
                    return receipt.Action == RentalItem.BlockDamagedAction
                        ? await BlockDamagedUnitsCoreAsync(order, item, receipt, cancellationToken)
                        : await MarkReturnedUnitsCoreAsync(order, item, receipt, cancellationToken);
                }
            case RentalItem.OverdueProtectionAction:
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item is null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await MarkAssetsOverdueCoreAsync(order, item, receipt, cancellationToken);
                }
            case "ExtendHold":
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await ExtendHoldCoreAsync(order, item, receipt, cancellationToken);
                }

            case "SupplierAccept":
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
                    {
                        return Error.Conflict("ITEM_IN_TERMINAL_STATE", $"Mục thuê đã ở trạng thái {item.Status}; không thể áp dụng accept.");
                    }
                    var acceptResult = await ConfirmAllocationCoreAsync(order, item, receipt, receipt.OperationId, item.SupplierId, isReconciliation: true, cancellationToken);
                    return acceptResult.IsSuccess ? Result.Success() : acceptResult.Error;
                }

            case "ReleaseAllocation":
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await ReleaseAllocationCoreAsync(order, item, receipt, receipt.OperationId, cancellationToken);
                }

            case "CancelInvoice":
                return await CancelInvoiceCoreAsync(order, receipt, receipt.OperationId, cancellationToken);

            case "RequestRefund":
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");
                    return await RequestRefundCoreAsync(order, item, receipt, receipt.OperationId, cancellationToken);
                }

            case "PaymentExpiry":
                return await PaymentExpiryCoreAsync(order, receipt, cancellationToken);

            case RentalItem.SettleRentalAction:
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == receipt.TargetItemId);
                    if (item is null) return Error.NotFound("ITEM_NOT_FOUND", "Rental item was not found.");
                    return await SettleRentalCoreAsync(order, item, receipt, cancellationToken);
                }

            default:
                return Error.Validation("UNSUPPORTED_ACTION", $"Hành động {receipt.Action} không hỗ trợ scheduled execution.");
        }
    }

    public async Task<Result<AdminReplayResponse>> ExecuteAdminReplayAsync(
        Guid orderId,
        string operationId,
        Guid adminActorId,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (adminActorId == Guid.Empty)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu danh tính Admin hợp lệ.");
        }

        string trimmedReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedReason) || trimmedReason.Length > 1000)
        {
            return Error.Validation("INVALID_REASON", "Lý do replay là bắt buộc và tối đa 1000 ký tự.");
        }

        string normalizedIdempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedIdempotencyKey) || normalizedIdempotencyKey.Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và tối đa 64 ký tự.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{orderId}'.");

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationId);
        if (receipt == null) return Error.NotFound("OPERATION_NOT_FOUND", $"Không tìm thấy operation '{operationId}'.");

        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return Error.Conflict("OPERATION_ALREADY_SUCCEEDED", "Operation đã hoàn thành thành công (Succeeded); không thể replay.");
        }

        // Validate journal command payload
        if (string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            return Error.Unprocessable("CORRUPTED_JOURNAL", "Journal payload bị thiếu; không thể replay an toàn.");
        }

        try
        {
            using var doc = JsonDocument.Parse(receipt.PayloadJson);
        }
        catch
        {
            return Error.Unprocessable("CORRUPTED_JOURNAL", "Journal payload bị hỏng; không thể replay an toàn.");
        }

        // Idempotency check for replay request
        var existingReplayAudit = order.StateTransitions.FirstOrDefault(t => t.OperationId == normalizedIdempotencyKey);
        if (existingReplayAudit != null)
        {
            if (string.Equals(existingReplayAudit.ToState, RentalOperationReceipt.StatusPending, StringComparison.OrdinalIgnoreCase) &&
                existingReplayAudit.Reason != null &&
                existingReplayAudit.Reason.Contains(operationId, StringComparison.OrdinalIgnoreCase) &&
                existingReplayAudit.Reason.Contains(trimmedReason, StringComparison.OrdinalIgnoreCase))
            {
                return new AdminReplayResponse(order.Id, receipt.OperationId, receipt.Action, receipt.Status, receipt.RetryCount, existingReplayAudit.OccurredAt);
            }

            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa Idempotency-Key đã được sử dụng với nội dung khác.");
        }

        // Audit transition
        order.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            order.Id,
            receipt.TargetItemId,
            receipt.Status,
            RentalOperationReceipt.StatusPending,
            "AdminReplayRequested",
            null,
            ActorRole.Admin,
            $"Admin {adminActorId} requested replay for {operationId} (Previous: {receipt.Status}/{receipt.ErrorClassification}, Retries: {receipt.RetryCount}). Reason: {trimmedReason}",
            utcNow,
            normalizedIdempotencyKey));

        // Reset retry budget to 0, status to Pending, NextRetryAt to now, KEEP IsOutcomeUncertain!
        receipt.ReopenForManualReplay(utcNow);

        await CommitLocalChangesAsync(order, cancellationToken);

        _logger.LogInformation("Admin {AdminId} successfully scheduled replay for Operation {OpId} (Order: {OrderId})",
            adminActorId, operationId, order.Id);

        return new AdminReplayResponse(order.Id, receipt.OperationId, receipt.Action, receipt.Status, receipt.RetryCount, utcNow);
    }

    public async Task<Result> ReplayStoredOperationAsync(
        Guid orderId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        OperationReceiptDescriptor? desc = null;

        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
            if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

            var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationId);
            if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

            receipt.ReopenForManualReplay(utcNow);
            await CommitLocalChangesAsync(order, cancellationToken);

            desc = new OperationReceiptDescriptor(
                receipt.Id,
                order.Id,
                receipt.OperationId,
                receipt.Action,
                receipt.TargetItemId,
                receipt.Status,
                receipt.RetryCount,
                receipt.NextRetryAt,
                receipt.CreatedAt,
                receipt.PrerequisiteOperationId,
                receipt.IsOutcomeUncertain);
        }

        return await ExecuteScheduledOperationAsync(desc!, cancellationToken);
    }
    private static void RecordExecutionFailure(RentalOperationReceipt receipt, Error error, DateTimeOffset utcNow, bool forceUncertain = false)
    {
        bool isUncertain = forceUncertain ||
                           receipt.IsOutcomeUncertain ||
                           error.Type == ErrorType.ServiceUnavailable ||
                           error.Code.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                           error.Code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);

        bool isExhausted = receipt.RetryCount >= 10;

        double baseDelay = Math.Min(60, Math.Pow(2, receipt.RetryCount));
        int jitterMs = Random.Shared.Next(0, 1000);
        DateTimeOffset? nextRetry = isExhausted ? null : utcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

        if (isUncertain)
        {
            receipt.RecordAttemptFailure(error.Code, error.Message, utcNow, nextRetry, isUncertain: true, isExhausted: isExhausted);
            return;
        }

        if (error.Type == ErrorType.Failure)
        {
            receipt.RecordAttemptFailure(error.Code, error.Message, utcNow, nextRetry, isUncertain: false, isExhausted: isExhausted);
            return;
        }

        // Deterministic Business/Validation errors without prior uncertainty do not retry
        receipt.MarkFailed(error.Code, error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
    }

    private async Task CommitLocalChangesAsync(RentalOrder order, CancellationToken cancellationToken)
    {
        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
