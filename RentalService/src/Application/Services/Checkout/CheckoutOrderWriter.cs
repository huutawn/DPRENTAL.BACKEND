#pragma warning disable CA1848, CA1873
using System.Text.Encodings.Web;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Checkout;

public sealed class CheckoutOrderWriter : ICheckoutOrderWriter
{
    // Giữ nguyên chữ có dấu để chuỗi không phình quá giới hạn 256 ký tự của RefundDestinationRef.
    private static readonly JsonSerializerOptions RefundDestinationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRentalOrderRepository _orders;
    private readonly IRentalCartRepository _carts;
    private readonly IOrderCodeGenerator _orderCodes;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly ILogger<CheckoutOrderWriter> _logger;

    public CheckoutOrderWriter(
        IRentalOrderRepository orders,
        IRentalCartRepository carts,
        IOrderCodeGenerator orderCodes,
        ILogger<CheckoutOrderWriter> logger)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orderCodes = orderCodes ?? throw new ArgumentNullException(nameof(orderCodes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CheckoutOrderWriter(
        IRentalOrderRepository orders,
        IRentalCartRepository carts,
        IOrderCodeGenerator orderCodes,
        IUnitOfWork unitOfWork,
        ILogger<CheckoutOrderWriter> logger)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orderCodes = orderCodes ?? throw new ArgumentNullException(nameof(orderCodes));
        _unitOfWork = unitOfWork;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        return CreateOrderAsync(
            plan,
            heldLines,
            checkoutOperationId,
            Guid.NewGuid(),
            utcNow.Add(CheckoutRules.PaymentWindow),
            utcNow,
            cancellationToken);
    }

    public async Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        Guid expectedRentalOrderId,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset orderCreatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(heldLines);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutOperationId);

        try
        {
            return await SaveOrderAsync(
                plan,
                heldLines,
                checkoutOperationId,
                expectedRentalOrderId,
                holdExpiresAt,
                orderCreatedAt,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checkout {CheckoutOperationId} hỏng trong lúc lưu đơn hàng.", checkoutOperationId);
            _unitOfWork?.DiscardChanges();
            throw;
        }
    }

    private async Task<Result<RentalOrder>> SaveOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        Guid expectedRentalOrderId,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset orderCreatedAt,
        CancellationToken cancellationToken)
    {
        RentalOrder? existingOrder = await _orders.GetByCheckoutOperationIdAsync(checkoutOperationId, cancellationToken);
        if (existingOrder is not null && existingOrder.RenterId == plan.RenterId)
        {
            return existingOrder;
        }

        RentalCart? cart = await _carts.GetActiveByRenterIdAsync(plan.RenterId, cancellationToken);
        if (cart is null || cart.Id != plan.CartId)
        {
            return await FailAsync(heldLines, RentalErrors.CartNotFound);
        }

        string orderCode = await _orderCodes.NextAsync(orderCreatedAt, cancellationToken);

        Result<RentalOrder> order = RentalOrder.Create(
            expectedRentalOrderId,
            orderCode,
            plan.RenterId,
            plan.Currency,
            CheckoutRules.DeliveryMethod,
            plan.Delivery.RecipientName!,
            plan.Delivery.RecipientPhone!,
            $"{plan.Delivery.AddressDetail}, {plan.Delivery.Province}",
            plan.Delivery.Note,
            JsonSerializer.Serialize(plan.RefundAccount, RefundDestinationJsonOptions),
            plan.TermsVersion,
            plan.TermsDocumentRef,
            plan.TermsContentHash,
            checkoutOperationId,
            orderCreatedAt,
            heldLines.Select(ToInitialItem),
            paymentDeadline: holdExpiresAt);
        if (order.IsFailure)
        {
            return await FailAsync(heldLines, order.Error);
        }

        Result cartResult = cart.CompleteCheckout(plan.Lines.Select(l => l.CartItemId), orderCreatedAt);
        if (cartResult.IsFailure)
        {
            _unitOfWork?.DiscardChanges();
            return await FailAsync(heldLines, cartResult.Error);
        }

        await _orders.CreateAsync(order.Value, cancellationToken);
        if (_unitOfWork is not null)
        {
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _unitOfWork.DiscardChanges();

                RentalOrder? existing = await _orders.GetByCheckoutOperationIdAsync(checkoutOperationId, cancellationToken);
                if (existing is not null && existing.RenterId == plan.RenterId)
                {
                    return existing;
                }

                _logger.LogWarning(ex, "Không lưu được đơn hàng cho checkout {CheckoutOperationId}.", checkoutOperationId);
                return await FailAsync(heldLines, CheckoutErrors.OrderNotSaved);
            }
        }

        return order;
    }

    private static Task<Result<RentalOrder>> FailAsync(IReadOnlyList<CheckoutHeldLine> heldLines, Error error) =>
        Task.FromResult<Result<RentalOrder>>(error);

    private static InitialRentalItemDto ToInitialItem(CheckoutHeldLine held) => new(
        held.RentalItemId,
        held.Line.EquipmentListingId,
        held.Line.SupplierId,
        held.Line.Quantity,
        held.Line.ExpectedStartDate,
        held.Line.ExpectedEndDate,
        held.Line.ListingName,
        null,
        held.Line.PickupLocationSnapshot,
        held.Line.DailyRate,
        held.Line.DepositPerUnit,
        held.AllocationGroupId,
        held.AssignedAssets
            .Select(a => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), a.AssetId, a.AllocationId))
            .ToList(),
        held.Line.ListingSnapshot);
}
