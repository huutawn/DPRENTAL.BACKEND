#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Operations;

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

    public static string ComputeFingerprint(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<Result> ReplayStoredOperationAsync(
        Guid orderId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        string action;
        Guid? targetItemId;

        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
            if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

            var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationId);
            if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

            action = receipt.Action;
            targetItemId = receipt.TargetItemId;
            DateTimeOffset utcNow = _timeProvider.GetUtcNow();

            // Operational replay with audit reopens retry budget
            receipt.RecordRetryAttempt(utcNow, null, "Operational replay requested", RentalOperationReceipt.ClassificationTransient);

            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(),
                order.Id,
                receipt.TargetItemId,
                receipt.Status,
                receipt.Status,
                "ReplayOperation",
                null,
                ActorRole.Admin,
                $"Manual operational replay for operation {operationId} (Action: {receipt.Action})",
                utcNow,
                operationId));

            await CommitLocalChangesAsync(order, cancellationToken);
        }

        switch (action)
        {
            case "ExtendHold":
                return await ExecuteExtendHoldAsync(orderId, targetItemId!.Value, operationId, cancellationToken);

            case "SupplierAccept":
                return await ExecuteReconcileAcceptAsync(orderId, targetItemId!.Value, operationId, cancellationToken);

            case "CancelInvoice":
                return await ExecuteCancelInvoiceAsync(orderId, operationId, cancellationToken);

            case "ReleaseAllocation":
                return await ExecuteReleaseAllocationAsync(orderId, targetItemId!.Value, operationId, cancellationToken);

            case "RequestRefund":
                return await ExecuteRequestRefundAsync(orderId, targetItemId!.Value, operationId, cancellationToken);

            default:
                return Error.Validation("UNSUPPORTED_ACTION", $"Hành động {action} không hỗ trợ replay.");
        }
    }

    private static void RecordExecutionFailure(RentalOperationReceipt receipt, Error error, DateTimeOffset utcNow)
    {
        bool isUncertain = error.Type == ErrorType.ServiceUnavailable ||
                           error.Code.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                           error.Code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);

        if (isUncertain)
        {
            double baseDelay = Math.Min(60, Math.Pow(2, receipt.RetryCount));
            int jitterMs = Random.Shared.Next(0, 1000);
            DateTimeOffset nextRetry = utcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

            receipt.RecordRetryAttempt(utcNow, nextRetry, error.Message, RentalOperationReceipt.ClassificationUncertain);
            if (receipt.RetryCount >= 10)
            {
                receipt.MarkFailed("UNCERTAIN_EXHAUSTED", $"Đã hết 10 lần thử lại không chắc chắn: {error.Message}", utcNow, RentalOperationReceipt.ClassificationUncertainExhausted, isTransient: false);
            }
            return;
        }

        if (error.Type == ErrorType.Failure)
        {
            double baseDelay = Math.Min(60, Math.Pow(2, receipt.RetryCount));
            int jitterMs = Random.Shared.Next(0, 1000);
            DateTimeOffset nextRetry = utcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

            receipt.RecordRetryAttempt(utcNow, nextRetry, error.Message, RentalOperationReceipt.ClassificationTransient);
            if (receipt.RetryCount >= 10)
            {
                receipt.MarkFailed("MAX_RETRIES_EXCEEDED", $"Đã thử lại 10 lần: {error.Message}", utcNow, RentalOperationReceipt.ClassificationTransient, isTransient: false);
            }
            return;
        }

        // Business/Validation errors do not retry
        receipt.MarkFailed(error.Code, error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
    }

    private async Task CommitLocalChangesAsync(RentalOrder order, CancellationToken cancellationToken)
    {
        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
