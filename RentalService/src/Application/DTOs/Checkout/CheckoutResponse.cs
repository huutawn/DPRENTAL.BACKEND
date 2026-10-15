using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>Đơn hàng vừa tạo (hoặc đã tạo từ lần gửi trước). Không chứa mã máy hay mã giữ lịch nội bộ.</summary>
/// <param name="OrderId">Mã định danh duy nhất của đơn hàng.</param>
/// <param name="OrderCode">Mã đơn hàng hiển thị cho người dùng, dạng DH-YYYY-NNNNNN.</param>
/// <param name="Status">Trạng thái đơn hàng; đơn mới tạo là PENDING_PAYMENT.</param>
/// <param name="PaymentDeadline">Hạn thanh toán, cũng là hạn giữ lịch (thời điểm gửi đơn + 30 phút).</param>
/// <param name="InvoiceRef">Mã hóa đơn bên PaymentService, dùng để thanh toán.</param>
/// <param name="Currency">Loại tiền của đơn hàng (VND).</param>
/// <param name="TotalRentalAmount">Tổng tiền thuê của mọi dòng.</param>
/// <param name="TotalDepositAmount">Tổng tiền cọc của mọi dòng.</param>
/// <param name="TotalAmount">Số tiền phải thanh toán = tổng tiền thuê + tổng tiền cọc.</param>
/// <param name="IsReplay">true khi đơn đã được tạo từ lần gửi trước với cùng Idempotency-Key.</param>
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

public sealed record CheckoutOperationAcceptedResponse(
    string OperationId,
    string StatusUrl);

public sealed record CheckoutOperationStatusResponse(
    string OperationId,
    string Status,
    Guid? OrderId = null,
    string? OrderCode = null,
    CheckoutResponse? Order = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? InvoiceStatus = null);
