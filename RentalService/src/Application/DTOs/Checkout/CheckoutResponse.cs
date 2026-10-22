using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>Thông tin thanh toán trả về để frontend render mã QR.</summary>
/// <param name="InvoiceRef">Mã hóa đơn bên PaymentService.</param>
/// <param name="Status">Trạng thái thanh toán (Pending).</param>
/// <param name="QrUrl">Đường dẫn ảnh QR VietQR.</param>
/// <param name="Bank">Ngân hàng thụ hưởng.</param>
/// <param name="Account">Số tài khoản thụ hưởng.</param>
/// <param name="TransferContent">Nội dung chuyển khoản.</param>
public sealed record CheckoutPaymentInfoDto(
    string InvoiceRef,
    string Status,
    string QrUrl,
    string Bank,
    string Account,
    string TransferContent);

/// <summary>Đơn hàng vừa tạo (hoặc đã tạo từ lần gửi trước).</summary>
/// <param name="OrderId">Mã định danh duy nhất của đơn hàng.</param>
/// <param name="OrderCode">Mã đơn hàng hiển thị cho người dùng, dạng DH-YYYY-NNNNNN.</param>
/// <param name="Status">Trạng thái đơn hàng (PendingPayment hoặc Paid).</param>
/// <param name="PaymentDeadline">Hạn thanh toán, cũng là hạn giữ lịch (thời điểm gửi đơn + 30 phút).</param>
/// <param name="Currency">Loại tiền của đơn hàng (VND).</param>
/// <param name="TotalAmount">Số tiền phải thanh toán.</param>
/// <param name="IsReplay">true khi đơn đã được tạo từ lần gửi trước với cùng Idempotency-Key.</param>
/// <param name="Payment">Thông tin thanh toán và mã QR (null khi đơn có tổng tiền bằng 0).</param>
public sealed record CheckoutResponse(
    Guid OrderId,
    string OrderCode,
    string Status,
    DateTimeOffset PaymentDeadline,
    string Currency,
    decimal TotalAmount,
    bool IsReplay,
    CheckoutPaymentInfoDto? Payment = null)
{
    public static CheckoutResponse From(RentalOrder order, bool isReplay, CheckoutPaymentInfoDto? payment = null)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new(
            order.Id,
            order.OrderCode,
            order.Status.ToString(),
            order.PaymentDeadline,
            order.Currency,
            order.TotalPayableAmount,
            isReplay,
            payment);
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
    string? InvoiceStatus = null,
    bool ReadyForPayment = false);
