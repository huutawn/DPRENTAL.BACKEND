#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.RentalService.Application.Services.Checkout;

public sealed class CheckoutService : ICheckoutService
{
    private static readonly TimeSpan BudgetDuration = TimeSpan.FromSeconds(20);
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
    private readonly IInventoryRentalClient _inventoryClient;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly ICheckoutOrderWriter _orderWriter;
    private readonly IRentalEventProducer _rentalEvents;
    private readonly ICheckoutInvoiceIssuer _invoiceIssuer;
    private readonly IPaymentRentalClient? _paymentClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckoutService> _logger;

    public CheckoutService(
        ICheckoutPreparationService preparation,
        IRentalCheckoutOperationRepository operationRepository,
        IRentalOrderRepository orders,
        IOrderLockManager locks,
        IInventoryRentalClient inventoryClient,
        IInventoryCommandProducer inventoryCommands,
        ICheckoutOrderWriter orderWriter,
        IRentalEventProducer rentalEvents,
        ICheckoutInvoiceIssuer invoiceIssuer,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null,
        IPaymentRentalClient? paymentClient = null,
        ILogger<CheckoutService>? logger = null)
    {
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _operationRepository = operationRepository ?? throw new ArgumentNullException(nameof(operationRepository));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _inventoryClient = inventoryClient ?? throw new ArgumentNullException(nameof(inventoryClient));
        _inventoryCommands = inventoryCommands ?? throw new ArgumentNullException(nameof(inventoryCommands));
        _orderWriter = orderWriter ?? throw new ArgumentNullException(nameof(orderWriter));
        _rentalEvents = rentalEvents ?? throw new ArgumentNullException(nameof(rentalEvents));
        _invoiceIssuer = invoiceIssuer ?? throw new ArgumentNullException(nameof(invoiceIssuer));
        _paymentClient = paymentClient;
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CheckoutService>.Instance;
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

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(BudgetDuration);
        var budgetToken = budgetCts.Token;

        try
        {
            IAsyncDisposable renterLock;
            try
            {
                renterLock = await _locks.AcquireRenterLockAsync(renter.RenterId, LockTimeout, budgetToken);
            }
            catch (TimeoutException)
            {
                return CheckoutErrors.InProgress;
            }

            await using (renterLock)
            {
                // 1. Idempotency replay check trước khi kiểm tra giỏ hàng
                var existingOp = await _operationRepository.GetByOperationIdAsync(operationId, budgetToken);
                if (existingOp is not null)
                {
                    if (existingOp.RenterUserId != renter.RenterId || existingOp.RequestFingerprint != fingerprint)
                    {
                        return CheckoutErrors.IdempotencyKeyReused;
                    }

                    IAsyncDisposable existingOrderLock;
                    try
                    {
                        existingOrderLock = await _locks.AcquireOrderLockAsync(existingOp.ExpectedRentalOrderId, LockTimeout, budgetToken);
                    }
                    catch (TimeoutException)
                    {
                        return CheckoutErrors.InProgress;
                    }

                    await using (existingOrderLock)
                    {
                        return await ContinueOrReplayOperationAsync(existingOp, isReplay: true, budgetToken);
                    }
                }

                // 2. Kiểm tra giới hạn: Mỗi renter chỉ có 1 checkout đang chạy hoặc 1 đơn chờ thanh toán
                var activeOp = await _operationRepository.GetActiveOperationByRenterAsync(renter.RenterId, budgetToken);
                if (activeOp is not null)
                {
                    return Error.Conflict("ACTIVE_CHECKOUT_IN_PROGRESS", "Bạn đang có một thao tác gửi đơn đang được xử lý.");
                }

                var pendingOrder = await _orders.GetPendingPaymentByRenterIdAsync(renter.RenterId, budgetToken);
                if (pendingOrder is not null)
                {
                    return CheckoutPreparationResult.PendingOrder(pendingOrder.OrderCode);
                }

                // 3. Chuẩn bị checkout (snapshot, bảng giá, giỏ hàng)
                var prepared = await _preparation.PrepareAsync(renter, request, budgetToken);
                if (prepared.IsFailure)
                {
                    return prepared;
                }

                var plan = prepared.Plan;
                var expectedOrderId = Guid.NewGuid();
                var utcNow = _timeProvider.GetUtcNow();
                var holdExpiresAt = utcNow.Add(CheckoutRules.PaymentWindow);

                IAsyncDisposable orderLock;
                try
                {
                    orderLock = await _locks.AcquireOrderLockAsync(expectedOrderId, LockTimeout, budgetToken);
                }
                catch (TimeoutException)
                {
                    return CheckoutErrors.InProgress;
                }

                await using (orderLock)
                {
                    var lineStatuses = plan.Lines.Select(line =>
                    {
                        var rentalItemId = Guid.NewGuid();
                        var opKey = $"rental-item:{rentalItemId}";
                        return new CheckoutItemHoldDescriptor(
                            CartItemId: line.CartItemId,
                            RentalItemId: rentalItemId,
                            EquipmentListingId: line.EquipmentListingId,
                            OperationKey: opKey,
                            Status: "Pending");
                    }).ToList();

                    // Lưu operation, request fingerprint, order/item IDs, snapshot và deadline trước HTTP call đầu tiên
                    var checkoutOp = new RentalCheckoutOperation(
                        operationId: operationId,
                        renterUserId: renter.RenterId,
                        requestFingerprint: fingerprint,
                        expectedRentalOrderId: expectedOrderId,
                        holdExpiresAt: holdExpiresAt,
                        preparationSnapshotJson: JsonSerializer.Serialize(plan, JsonOptions),
                        itemHoldStatusesJson: JsonSerializer.Serialize(lineStatuses, JsonOptions),
                        createdAt: utcNow);

                    await _operationRepository.AddAsync(checkoutOp, budgetToken);
                    await _unitOfWork.SaveChangesAsync(budgetToken);

                    return await ExecuteOrchestrationAsync(checkoutOp, plan, lineStatuses, isReplay: false, budgetToken);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckoutResult.CheckoutNotReady(operationId);
        }
    }

    public async Task<CheckoutSubmitResult> ContinueOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var op = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
        if (op is null)
        {
            return Error.NotFound("CHECKOUT_OPERATION_NOT_FOUND", "Không tìm thấy thao tác gửi đơn.");
        }

        IAsyncDisposable renterLock;
        try
        {
            renterLock = await _locks.AcquireRenterLockAsync(op.RenterUserId, LockTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return CheckoutResult.CheckoutNotReady(operationId);
        }

        await using (renterLock)
        await using (var orderLock = await _locks.AcquireOrderLockAsync(op.ExpectedRentalOrderId, LockTimeout, cancellationToken))
        {
            var currentOp = await _operationRepository.GetByOperationIdAsync(operationId, cancellationToken);
            if (currentOp is null)
            {
                return Error.NotFound("CHECKOUT_OPERATION_NOT_FOUND", "Không tìm thấy thao tác gửi đơn.");
            }

            return await ContinueOrReplayOperationAsync(currentOp, isReplay: false, cancellationToken);
        }
    }

    private async Task<CheckoutSubmitResult> ContinueOrReplayOperationAsync(
        RentalCheckoutOperation op,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow();

        if (op.Status == CheckoutOperationStatus.Failed)
        {
            return CheckoutResult.Failure(Error.Conflict(op.ErrorCode ?? "FAILED", op.ErrorMessage ?? "Thao tác gửi đơn thất bại."));
        }

        if (op.Status == CheckoutOperationStatus.TimedOut)
        {
            return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
        }

        var order = await _orders.GetByIdWithDetailsAsync(op.ExpectedRentalOrderId, cancellationToken);
        if (order is not null)
        {
            if (order.Status is RentalOrderStatus.Expired or RentalOrderStatus.Cancelled)
            {
                return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_" + order.Status.ToString().ToUpperInvariant(), $"Đơn hàng {order.OrderCode} ở trạng thái {order.Status}."));
            }

            if (order.Status == RentalOrderStatus.Paid)
            {
                if (order.TotalPayableAmount == 0)
                {
                    return CheckoutResponse.From(order, isReplay, payment: null);
                }
                return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_PAID", $"Đơn hàng {order.OrderCode} đã được thanh toán."));
            }

            if (utcNow >= order.PaymentDeadline)
            {
                return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
            }


            return await EnsurePaymentAndQrAsync(op, order, isReplay, cancellationToken);
        }

        if (utcNow >= op.HoldExpiresAt)
        {
            await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                op.MarkTimedOut(utcNow);
                var lineDescs = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions) ?? [];
                var heldItemIds = lineDescs.Where(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)).Select(l => l.RentalItemId);
                await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                await _operationRepository.UpdateAsync(op, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await failTx.CommitAsync(cancellationToken);
            }
            return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
        }

        var plan = JsonSerializer.Deserialize<CheckoutPlan>(op.PreparationSnapshotJson, JsonOptions);
        var lineStatuses = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(op.ItemHoldStatusesJson, JsonOptions);
        if (plan is null || lineStatuses is null)
        {
            return Error.Failure("CHECKOUT_CORRUPTED", "Dữ liệu checkout operation bị hỏng.");
        }

        return await ExecuteOrchestrationAsync(op, plan, lineStatuses, isReplay, cancellationToken);
    }

    private async Task<CheckoutSubmitResult> ExecuteOrchestrationAsync(
        RentalCheckoutOperation op,
        CheckoutPlan plan,
        List<CheckoutItemHoldDescriptor> lineStatuses,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow();

        // 1. HTTP Inventory: hold từng dòng tuần tự
        for (int i = 0; i < plan.Lines.Count; i++)
        {
            var line = plan.Lines[i];
            var desc = lineStatuses.FirstOrDefault(l => l.CartItemId == line.CartItemId)
                ?? lineStatuses[i];

            if (string.Equals(desc.Status, "Held", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (utcNow >= op.HoldExpiresAt)
            {
                return CheckoutResult.Failure(CheckoutErrors.OrderExpired);
            }

            var (holdStartDate, holdEndDate) = RentalInventoryDateAdapter
                .ToInventoryDates(line.ExpectedStartDate, line.ExpectedEndDate).Value;

            var holdRequest = new CreateHoldRequest(
                ListingId: line.EquipmentListingId,
                Quantity: line.Quantity,
                StartDate: holdStartDate,
                EndDate: holdEndDate,
                HoldExpiresAt: op.HoldExpiresAt,
                SourceSystem: InventoryContractSpec.SourceSystem,
                GroupReference: $"rental-item:{desc.RentalItemId}",
                Notes: $"Order checkout: item {desc.RentalItemId}");

            Result<CreateHoldResponse> holdResult;
            try
            {
                holdResult = await _inventoryClient.CreateHoldAsync(holdRequest, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CheckoutResult.CheckoutNotReady(op.OperationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi gọi HTTP hold cho item {RentalItemId}", desc.RentalItemId);
                return CheckoutResult.CheckoutNotReady(op.OperationId);
            }

            if (holdResult.IsFailure)
            {
                if (IsTemporary(holdResult.Error))
                {
                    return CheckoutResult.CheckoutNotReady(op.OperationId);
                }

                await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
                {
                    op.MarkFailed(holdResult.Error.Code, holdResult.Error.Message, _timeProvider.GetUtcNow());
                    var heldItemIds = lineStatuses.Where(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)).Select(l => l.RentalItemId);
                    await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                    await _operationRepository.UpdateAsync(op, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await failTx.CommitAsync(cancellationToken);
                }

                var conflicts = new List<CheckoutHoldConflict>
                {
                    new(line.CartItemId, line.EquipmentListingId, line.Quantity,
                        CheckoutHoldConflictCode.InsufficientAvailability, holdResult.Error.Message)
                };
                return CheckoutResult.HoldFailure(holdResult.Error, conflicts);
            }

            var holdResp = holdResult.Value;
            if (holdResp.ListingId != line.EquipmentListingId ||
                holdResp.Quantity != line.Quantity ||
                holdResp.Allocations == null ||
                holdResp.Allocations.Count != line.Quantity ||
                holdResp.Allocations.Any(a => !string.Equals(a.Status, "Held", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("Inventory hold response không khớp dữ liệu cho line {RentalItemId}", desc.RentalItemId);
                await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
                {
                    op.MarkFailed("INVALID_HOLD_RESPONSE", "Inventory trả dữ liệu giữ chỗ không hợp lệ.", _timeProvider.GetUtcNow());
                    var heldItemIds = lineStatuses.Where(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)).Select(l => l.RentalItemId);
                    await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                    await _operationRepository.UpdateAsync(op, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await failTx.CommitAsync(cancellationToken);
                }
                return CheckoutResult.Failure(Error.Conflict("INVALID_HOLD_RESPONSE", "Inventory trả dữ liệu giữ chỗ không hợp lệ."));
            }

            var allocMappings = holdResp.Allocations
                .Select(a => new AllocationAssetMappingPayload(a.Id, a.AssetId, string.Empty))
                .ToList();

            var updatedDesc = desc with
            {
                Status = "Held",
                Allocations = allocMappings
            };
            int descIdx = lineStatuses.FindIndex(l => l.RentalItemId == desc.RentalItemId);
            lineStatuses[descIdx] = updatedDesc;

            op.UpdateItemHoldStatuses(JsonSerializer.Serialize(lineStatuses, JsonOptions), _timeProvider.GetUtcNow());
            await _operationRepository.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // 2. Xác nhận tất cả dòng đã giữ thành công
        if (!lineStatuses.All(l => string.Equals(l.Status, "Held", StringComparison.OrdinalIgnoreCase)))
        {
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }

        // 3. Lưu order, assignments, event Outbox và receipt invoice
        var order = await _orders.GetByIdWithDetailsAsync(op.ExpectedRentalOrderId, cancellationToken);
        if (order is null)
        {
            var heldLines = plan.Lines.Select(line =>
            {
                var desc = lineStatuses.First(l => l.CartItemId == line.CartItemId);
                var assignedAssets = desc.Allocations!
                    .Select(a => new AssignedAssetDto(a.AllocationId, a.AssetId, "Held"))
                    .ToList();
                return new CheckoutHeldLine(line, desc.RentalItemId, desc.OperationKey, assignedAssets);
            }).ToList();

            DateTimeOffset orderCreatedAt = _timeProvider.GetUtcNow();
            await using var createTx = await _unitOfWork.BeginTransactionAsync(cancellationToken);

            var orderResult = await _orderWriter.CreateOrderAsync(
                plan,
                heldLines,
                op.OperationId,
                op.ExpectedRentalOrderId,
                op.HoldExpiresAt,
                orderCreatedAt,
                cancellationToken);

            if (orderResult.IsFailure)
            {
                await createTx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();

                await using (var failTx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
                {
                    op.MarkFailed(orderResult.Error.Code, orderResult.Error.Message, _timeProvider.GetUtcNow());
                    var heldItemIds = lineStatuses.Select(l => l.RentalItemId);
                    await StageCheckoutReleasesAsync(op.ExpectedRentalOrderId, heldItemIds, cancellationToken);
                    await _operationRepository.UpdateAsync(op, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await failTx.CommitAsync(cancellationToken);
                }

                return CheckoutResult.Failure(orderResult.Error);
            }

            order = orderResult.Value;

            var enqueueEventRes = await _rentalEvents.EnqueueRentalOrderCreatedAsync(
                new RentalOrderCreatedPayload(
                    order.Id,
                    order.OrderCode,
                    order.RenterId,
                    order.Currency,
                    order.TotalRentalAmount,
                    order.TotalDepositAmount,
                    order.TotalPayableAmount,
                    order.PaymentDeadline,
                    order.CreatedAt,
                    order.Items.Select(item => new RentalOrderCreatedItemPayload(
                        item.Id,
                        item.EquipmentListingId,
                        item.SupplierId,
                        item.Quantity,
                        item.ExpectedStartDate,
                        item.ExpectedEndDate,
                        item.RentalAmount,
                        item.DepositAmount)).ToArray()),
                correlationId: order.Id.ToString("D"),
                cancellationToken: cancellationToken);

            if (enqueueEventRes.IsFailure)
            {
                await createTx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return CheckoutResult.Failure(enqueueEventRes.Error);
            }

            string invoiceOpKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
            var invoiceReceipt = RentalOperationReceipt.CreatePending(
                Guid.NewGuid(),
                order.Id,
                invoiceOpKey,
                "CreateInvoice",
                RentalOperationExecutor.ComputeFingerprint($"CreateInvoice:{order.Id}"),
                orderCreatedAt,
                targetItemId: null,
                payloadJson: null);

            order.AddOperationReceipt(invoiceReceipt);
            op.MarkSucceeded(orderCreatedAt, invoiceStatus: "Pending");

            await _orders.UpdateAsync(order, cancellationToken);
            await _operationRepository.UpdateAsync(op, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await createTx.CommitAsync(cancellationToken);
        }

        // 4. HTTP Payment: tạo invoice, nhận cả qrUrl (commit trước khi gọi Payment; không giữ db tx)
        return await EnsurePaymentAndQrAsync(op, order, isReplay, cancellationToken);
    }

    private async Task<CheckoutSubmitResult> EnsurePaymentAndQrAsync(
        RentalCheckoutOperation op,
        RentalOrder order,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow();

        if (order.TotalPayableAmount == 0)
        {
            var zeroInvoiceRes = await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
            if (zeroInvoiceRes.IsSuccess)
            {
                var zeroInv = zeroInvoiceRes.Value;
                if (order.Status == RentalOrderStatus.PendingPayment)
                {
                    order.MarkAsPaid(zeroInv.InvoiceRef, zeroInv.InvoiceRef, utcNow, utcNow);
                    await _orders.UpdateAsync(order, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }
            }
            return CheckoutResponse.From(order, isReplay, payment: null);
        }

        Result<CreateInvoiceResponse> invoiceResult;
        try
        {
            invoiceResult = await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi gọi payment cho order {OrderId}", order.Id);
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }

        if (invoiceResult.IsFailure)
        {
            if (IsTemporary(invoiceResult.Error))
            {
                return CheckoutResult.CheckoutNotReady(op.OperationId);
            }
            return CheckoutResult.Failure(invoiceResult.Error);
        }

        var invoice = invoiceResult.Value;

        if (string.IsNullOrWhiteSpace(invoice.InvoiceRef) ||
            invoice.TotalAmount != order.TotalPayableAmount ||
            !string.Equals(invoice.Currency, order.Currency, StringComparison.OrdinalIgnoreCase) ||
            invoice.PaymentDeadline != order.PaymentDeadline)
        {
            _logger.LogError("Payment invoice không khớp dữ liệu cho order {OrderId}", order.Id);
            return CheckoutResult.Failure(CheckoutErrors.InvoiceRejected);
        }

        if (string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase))
        {
            return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_PAID", $"Đơn hàng {order.OrderCode} đã được thanh toán."));
        }
        if (string.Equals(invoice.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(invoice.Status, "Expired", StringComparison.OrdinalIgnoreCase) ||
            utcNow >= order.PaymentDeadline)
        {
            return CheckoutResult.Failure(Error.Conflict("ORDER_ALREADY_" + invoice.Status.ToUpperInvariant(), $"Đơn hàng {order.OrderCode} ở trạng thái {invoice.Status}."));
        }

        if (string.IsNullOrWhiteSpace(invoice.QrUrl))
        {
            return CheckoutResult.CheckoutNotReady(op.OperationId);
        }

        var paymentInfo = new CheckoutPaymentInfoDto(
            InvoiceRef: invoice.InvoiceRef,
            Status: invoice.Status,
            QrUrl: invoice.QrUrl,
            Bank: invoice.Bank ?? string.Empty,
            Account: invoice.Account ?? string.Empty,
            TransferContent: invoice.TransferContent ?? string.Empty);

        return CheckoutResponse.From(order, isReplay, paymentInfo);
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
            CheckoutPaymentInfoDto? paymentInfo = null;
            if (hasValidInvoice)
            {
                invoiceStatus = "Ready";
                paymentInfo = new CheckoutPaymentInfoDto(
                    order.InvoiceRef!,
                    "Ready",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
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

            var orderDto = CheckoutResponse.From(order, false, paymentInfo);
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

    private async Task<Result> StageCheckoutReleasesAsync(
        Guid orderId,
        IEnumerable<Guid> rentalItemIds,
        CancellationToken ct)
    {
        foreach (Guid itemId in rentalItemIds.Distinct().Order())
        {
            var release = new ReleaseAllocationCommandPayload(
                RentalOrderId: orderId,
                RentalItemId: itemId,
                OperationKey: $"checkout:compensation:{orderId:N}:{itemId:N}:release",
                Reason: "Checkout did not complete",
                AllocationGroupId: $"rental-item:{itemId}");

            var result = await _inventoryCommands.EnqueueReleaseAllocationAsync(
                release,
                correlationId: orderId.ToString("D"),
                cancellationToken: ct);

            if (result.IsFailure) return result.Error;
        }

        return Result.Success();
    }

    private static bool IsTemporary(Error error) =>
        error.Type is ErrorType.ServiceUnavailable or ErrorType.Failure;

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
