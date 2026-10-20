using System.Security.Cryptography;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Checkout;

public sealed class CheckoutService : ICheckoutService
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ICheckoutPreparationService _preparation;
    private readonly IRentalCheckoutOperationRepository _operationRepository;
    private readonly IRentalOrderRepository _orders;
    private readonly IOrderLockManager _locks;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CheckoutService(
        ICheckoutPreparationService preparation,
        IRentalCheckoutOperationRepository operationRepository,
        IRentalOrderRepository orders,
        IOrderLockManager locks,
        IInventoryCommandProducer inventoryCommands,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null)
    {
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _operationRepository = operationRepository ?? throw new ArgumentNullException(nameof(operationRepository));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _inventoryCommands = inventoryCommands ?? throw new ArgumentNullException(nameof(inventoryCommands));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CheckoutSubmitResult> SubmitAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (renter.RenterId == Guid.Empty)
        {
            return CheckoutErrors.Unauthenticated;
        }

        string operationId = CheckoutRules.DeriveOperationId(renter.RenterId, idempotencyKey);
        string fingerprint = ComputeRequestFingerprint(renter.RenterId, request);

        IAsyncDisposable renterLock;
        try
        {
            renterLock = await _locks.AcquireRenterLockAsync(renter.RenterId, LockTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return CheckoutErrors.InProgress;
        }

        await using (renterLock)
        {
            // 1. Idempotency replay check trước khi kiểm tra giỏ hàng
            var existingOp = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
            if (existingOp is not null)
            {
                if (existingOp.RenterUserId != renter.RenterId || existingOp.RequestFingerprint != fingerprint)
                {
                    return CheckoutErrors.IdempotencyKeyReused;
                }

                return new CheckoutOperationAcceptedResponse(
                    existingOp.OperationId,
                    $"/api/v1/rental/checkout/operations/{existingOp.OperationId}");
            }

            // 2. Kiểm tra giới hạn: Mỗi renter chỉ có 1 checkout đang chạy hoặc 1 đơn chờ thanh toán
            var activeOp = await _operationRepository.GetActiveOperationByRenterAsync(renter.RenterId, cancellationToken);
            if (activeOp is not null)
            {
                return Error.Conflict("ACTIVE_CHECKOUT_IN_PROGRESS", "Bạn đang có một thao tác gửi đơn đang được xử lý.");
            }

            var pendingOrder = await _orders.GetPendingPaymentByRenterIdAsync(renter.RenterId, cancellationToken);
            if (pendingOrder is not null)
            {
                return CheckoutPreparationResult.PendingOrder(pendingOrder.OrderCode);
            }

            // 3. Chuẩn bị checkout (snapshot, bảng giá, giỏ hàng)
            var prepared = await _preparation.PrepareAsync(renter, request, cancellationToken);
            if (prepared.IsFailure)
            {
                // Giữ nguyên chi tiết lỗi (fieldErrors, currentQuote, lines) để client hiển thị đúng chỗ.
                return prepared;
            }

            var plan = prepared.Plan;
            var expectedOrderId = Guid.NewGuid();
            var utcNow = _timeProvider.GetUtcNow();
            var holdExpiresAt = utcNow.Add(CheckoutRules.PaymentWindow);

            // 4. Mở transaction trước khi enqueue Outbox đầu tiên để đảm bảo nguyên tử toàn bộ
            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var lineStatuses = new List<CheckoutItemHoldDescriptor>(plan.Lines.Count);

                foreach (var line in plan.Lines)
                {
                    var rentalItemId = Guid.NewGuid();
                    var opKey = $"checkout:{expectedOrderId:N}:{rentalItemId:N}:hold";

                    // Đơn lưu [từ, đến) không tính ngày cuối; Inventory nhận ngày dùng máy cuối có tính.
                    // Thuê 18, 19, 20: đơn lưu [18, 21), gửi Inventory startDate 18, endDate 20.
                    var (holdStartDate, holdEndDate) = RentalInventoryDateAdapter
                        .ToInventoryDates(line.ExpectedStartDate, line.ExpectedEndDate).Value;

                    var holdCmd = new CreateHoldCommandPayload(
                        RentalOrderId: expectedOrderId,
                        RentalItemId: rentalItemId,
                        OperationKey: opKey,
                        EquipmentListingId: line.EquipmentListingId,
                        Quantity: line.Quantity,
                        StartDate: holdStartDate,
                        EndDate: holdEndDate,
                        HoldExpiresAt: holdExpiresAt,
                        Notes: $"Order checkout: item {rentalItemId}");

                    var enqueueResult = await _inventoryCommands.EnqueueCreateHoldAsync(
                        holdCmd,
                        correlationId: expectedOrderId.ToString("D"),
                        cancellationToken: cancellationToken);

                    if (enqueueResult.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return enqueueResult.Error;
                    }

                    lineStatuses.Add(new CheckoutItemHoldDescriptor(
                        CartItemId: line.CartItemId,
                        RentalItemId: rentalItemId,
                        EquipmentListingId: line.EquipmentListingId,
                        OperationKey: opKey,
                        Status: "Pending"));
                }

                // 5. Lưu bền vững RentalCheckoutOperation trong cùng transaction với Outbox
                var checkoutOp = new RentalCheckoutOperation(
                    operationId: operationId,
                    renterUserId: renter.RenterId,
                    requestFingerprint: fingerprint,
                    expectedRentalOrderId: expectedOrderId,
                    holdExpiresAt: holdExpiresAt,
                    preparationSnapshotJson: JsonSerializer.Serialize(plan, JsonOptions),
                    itemHoldStatusesJson: JsonSerializer.Serialize(lineStatuses, JsonOptions),
                    createdAt: utcNow);

                await _operationRepository.AddAsync(checkoutOp, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new CheckoutOperationAcceptedResponse(
                    operationId,
                    $"/api/v1/rental/checkout/operations/{operationId}");
            }
            catch (Exception)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                throw;
            }
        }
    }

    public async Task<Result<CheckoutOperationStatusResponse>> GetOperationStatusAsync(
        string operationId,
        Guid renterUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var op = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
        if (op is null || op.RenterUserId != renterUserId)
        {
            return Error.NotFound("CHECKOUT_OPERATION_NOT_FOUND", "Không tìm thấy thao tác gửi đơn.");
        }

        if (op.Status == CheckoutOperationStatus.Succeeded)
        {
            var order = await _orders.GetForCheckoutPollingAsync(op.ExpectedRentalOrderId, cancellationToken);
            if (order != null && order.RenterId != renterUserId)
            {
                order = null;
            }

            if (order is null)
            {
                return new CheckoutOperationStatusResponse(
                    OperationId: op.OperationId,
                    Status: op.Status.ToString(),
                    OrderId: null,
                    OrderCode: null,
                    Order: null,
                    InvoiceStatus: null,
                    ReadyForPayment: false);
            }

            var now = _timeProvider.GetUtcNow();
            bool hasValidInvoice = !string.IsNullOrWhiteSpace(order.InvoiceRef);
            bool readyForPayment = order.Status == RentalOrderStatus.PendingPayment
                && hasValidInvoice
                && now < order.PaymentDeadline;

            string invoiceStatus;
            if (hasValidInvoice)
            {
                invoiceStatus = "Ready";
            }
            else
            {
                string invoiceOpKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
                var invoiceReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == invoiceOpKey);

                if (order.Status is not RentalOrderStatus.PendingPayment || now >= order.PaymentDeadline)
                {
                    invoiceStatus = "Failed";
                }
                else if (invoiceReceipt?.Status == RentalOperationReceipt.StatusFailed &&
                         invoiceReceipt.ErrorClassification == RentalOperationReceipt.ClassificationBusiness)
                {
                    invoiceStatus = "Failed";
                }
                else
                {
                    invoiceStatus = "Pending";
                }
            }

            var orderDto = CheckoutResponse.From(order, false);
            return new CheckoutOperationStatusResponse(
                OperationId: op.OperationId,
                Status: op.Status.ToString(),
                OrderId: order.Id,
                OrderCode: order.OrderCode,
                Order: orderDto,
                InvoiceStatus: invoiceStatus,
                ReadyForPayment: readyForPayment);
        }

        if (op.Status == CheckoutOperationStatus.Failed)
        {
            return new CheckoutOperationStatusResponse(
                OperationId: op.OperationId,
                Status: op.Status.ToString(),
                ErrorCode: op.ErrorCode ?? "FAILED",
                ErrorMessage: op.ErrorMessage ?? "Thao tác gửi đơn thất bại.",
                ReadyForPayment: false);
        }

        if (op.Status == CheckoutOperationStatus.TimedOut)
        {
            return new CheckoutOperationStatusResponse(
                OperationId: op.OperationId,
                Status: op.Status.ToString(),
                ErrorCode: "TIMEOUT",
                ErrorMessage: "Hết hạn giữ lịch cho đơn hàng.",
                ReadyForPayment: false);
        }

        return new CheckoutOperationStatusResponse(
            OperationId: op.OperationId,
            Status: op.Status.ToString(),
            ReadyForPayment: false);
    }

    private static string ComputeRequestFingerprint(Guid renterId, CheckoutRequest request)
    {
        var wrapper = new { RenterId = renterId, Request = request };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wrapper, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public sealed record CheckoutItemHoldDescriptor(
    Guid CartItemId,
    Guid RentalItemId,
    Guid EquipmentListingId,
    string OperationKey,
    string Status,
    IReadOnlyList<AllocationAssetMappingPayload>? Allocations = null);
