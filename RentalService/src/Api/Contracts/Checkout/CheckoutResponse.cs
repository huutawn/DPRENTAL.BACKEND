using Dprental.RentalService.Application.Checkout;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Api.Contracts.Checkout;

/// <summary>Đơn hàng vừa tạo (hoặc đã tạo từ lần gửi trước). Không chứa mã máy hay mã giữ lịch nội bộ.</summary>
public sealed record CheckoutResponse(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    DateTimeOffset PaymentDeadline,
    string InvoiceRef,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    bool IsReplay)
{
    public static CheckoutResponse From(RentalOrder order, bool isReplay)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new(
            order.Id,
            order.OrderCode,
            order.Status,
            order.PaymentDeadline,
            order.InvoiceRef!,
            order.Currency,
            order.TotalRentalAmount,
            order.TotalDepositAmount,
            order.TotalPayableAmount,
            isReplay);
    }
}

/// <summary>Giá và số tiền hiện tại, trả kèm lỗi Checkout.PriceChanged để renter xem lại rồi gửi lại.</summary>
public sealed record CheckoutQuoteResponse(
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    IReadOnlyList<CheckoutQuoteLineResponse> Lines)
{
    public static CheckoutQuoteResponse From(CheckoutPlan quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        return new(
            quote.TotalRentalAmount,
            quote.TotalDepositAmount,
            quote.TotalAmount,
            quote.Lines
                .Select(l => new CheckoutQuoteLineResponse(
                    l.CartItemId,
                    l.EquipmentListingId,
                    l.Quantity,
                    l.RentalDays,
                    l.DailyRate,
                    l.DepositPerUnit,
                    l.RentalAmount,
                    l.DepositAmount))
                .ToList());
    }
}

public sealed record CheckoutQuoteLineResponse(
    Guid CartItemId,
    Guid EquipmentListingId,
    int Quantity,
    int RentalDays,
    decimal DailyRate,
    decimal DepositPerUnit,
    decimal RentalAmount,
    decimal DepositAmount);
