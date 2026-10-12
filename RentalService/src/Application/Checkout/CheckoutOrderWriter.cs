#pragma warning disable CA1848, CA1873
using System.Text.Encodings.Web;
using System.Text.Json;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Checkout;

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
    private readonly ICheckoutHoldOrchestrator _holds;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CheckoutOrderWriter> _logger;

    public CheckoutOrderWriter(
        IRentalOrderRepository orders,
        IRentalCartRepository carts,
        IOrderCodeGenerator orderCodes,
        ICheckoutHoldOrchestrator holds,
        IUnitOfWork unitOfWork,
        ILogger<CheckoutOrderWriter> logger)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orderCodes = orderCodes ?? throw new ArgumentNullException(nameof(orderCodes));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(heldLines);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutOperationId);

        RentalCart? cart = await _carts.GetActiveByRenterIdAsync(plan.RenterId, cancellationToken);
        if (cart is null || cart.Id != plan.CartId)
        {
            return await FailAsync(heldLines, RentalErrors.CartNotFound);
        }

        string orderCode = await _orderCodes.NextAsync(utcNow, cancellationToken);

        Result<RentalOrder> order = RentalOrder.Create(
            Guid.NewGuid(),
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
            utcNow,
            heldLines.Select(ToInitialItem));
        if (order.IsFailure)
        {
            return await FailAsync(heldLines, order.Error);
        }

        Result cartResult = cart.CompleteCheckout(plan.Lines.Select(l => l.CartItemId), utcNow);
        if (cartResult.IsFailure)
        {
            _unitOfWork.DiscardChanges();
            return await FailAsync(heldLines, cartResult.Error);
        }

        await _orders.CreateAsync(order.Value, cancellationToken);
        try
        {
            // Đơn, dòng thuê, phân bổ máy, điều khoản và thay đổi của giỏ được ghi trong cùng một transaction.
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _unitOfWork.DiscardChanges();

            // Lần gửi trùng (double-click) dùng chung hold với đơn đã tạo: trả đơn đó và không được nhả hold.
            RentalOrder? existing = await _orders.GetByCheckoutOperationIdAsync(checkoutOperationId, cancellationToken);
            if (existing is not null && existing.RenterId == plan.RenterId)
            {
                return existing;
            }

            _logger.LogWarning(ex, "Không lưu được đơn hàng cho checkout {CheckoutOperationId}.", checkoutOperationId);
            return await FailAsync(heldLines, CheckoutErrors.OrderNotSaved);
        }

        return order;
    }

    private async Task<Result<RentalOrder>> FailAsync(IReadOnlyList<CheckoutHeldLine> heldLines, Error error)
    {
        await _holds.ReleaseAllLinesAsync(heldLines);
        return error;
    }

    private static InitialRentalItemDto ToInitialItem(CheckoutHeldLine held) => new(
        held.RentalItemId,
        held.Line.EquipmentListingId,
        held.Line.SupplierId,
        held.Line.Quantity,
        held.Line.ExpectedStartDate,
        held.Line.ExpectedEndDate,
        held.Line.ListingName,
        null,
        held.Line.PickupLocation,
        held.Line.DailyRate,
        held.Line.DepositPerUnit,
        held.AllocationGroupId,
        held.AssignedAssets
            .Select(a => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), a.AssetId, a.AllocationId))
            .ToList(),
        held.Line.ListingSnapshot);
}
