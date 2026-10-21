using System;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Policies;

public static class LatePaymentAcceptancePolicy
{
    public static readonly TimeSpan CutoffWindow = TimeSpan.FromMinutes(15);

    public static DateTimeOffset CalculateCutoff(DateTimeOffset paymentDeadline) =>
        paymentDeadline.Add(CutoffWindow);

    public static bool IsCutoffExpired(DateTimeOffset paymentDeadline, DateTimeOffset utcNow) =>
        utcNow >= CalculateCutoff(paymentDeadline);

    public static Result ValidateEligibility(
        Invoice invoice,
        Guid rentalOrderId,
        string paymentRef,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentRef);

        if (invoice.RentalOrderId != rentalOrderId)
        {
            return Error.Conflict("RENTAL_ORDER_MISMATCH", "Hóa đơn không thuộc RentalOrderId tương ứng.");
        }

        string normalizedPaymentRef = paymentRef.Trim();
        if (!string.Equals(invoice.PaymentRef, normalizedPaymentRef, StringComparison.Ordinal))
        {
            return Error.Conflict("PAYMENT_REF_MISMATCH", "PaymentRef không khớp với khoản thanh toán đã ghi nhận.");
        }

        if (invoice.Status != InvoiceStatus.Expired &&
            !(invoice.Status == InvoiceStatus.Cancelled && string.Equals(invoice.CancellationOrigin, InvoiceCancellationOrigins.Timeout, StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Conflict("INVOICE_NOT_ELIGIBLE_FOR_LATE_ACCEPTANCE", "Chỉ hóa đơn ở trạng thái EXPIRED hoặc CANCELLED do TIMEOUT mới được chấp nhận thanh toán trễ.");
        }

        if (!string.Equals(invoice.CancellationOrigin, InvoiceCancellationOrigins.Timeout, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("INVALID_CANCELLATION_ORIGIN", "Chỉ chấp nhận thanh toán trễ đối với hóa đơn có origin là TIMEOUT.");
        }

        if (!string.Equals(invoice.Currency, "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("CURRENCY_MISMATCH", "Hóa đơn phải sử dụng đơn vị tiền tệ VND.");
        }

        if (IsCutoffExpired(invoice.PaymentDeadline, utcNow))
        {
            return Error.Conflict("LATE_PAYMENT_WINDOW_EXPIRED", "Đã quá hạn 15 phút kể từ deadline thanh toán để chấp nhận thanh toán trễ.");
        }

        return Result.Success();
    }

    public static Result ValidatePaymentTransaction(Invoice invoice, PaymentTransaction? pt)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (pt is null)
        {
            return Error.NotFound("PAYMENT_TRANSACTION_NOT_FOUND", "Không tìm thấy giao dịch ngân hàng tương ứng với PaymentRef.");
        }

        if (!string.Equals(pt.TransferType, "in", StringComparison.OrdinalIgnoreCase) || pt.TransferAmount <= 0)
        {
            return Error.Conflict("INVALID_TRANSFER_TYPE", "Giao dịch ngân hàng phải là giao dịch nạp tiền (in).");
        }

        if (pt.TransferAmount != invoice.TotalAmount)
        {
            return Error.Conflict("AMOUNT_MISMATCH", "Số tiền chuyển khoản không khớp toàn bộ số tiền của hóa đơn.");
        }

        // Must be in classification allowing late payment acceptance
        bool isEligibleClassification =
            (pt.Status == PaymentTransactionStatus.ManualRefundRequired && pt.ReasonCode == PaymentTransactionReason.LatePayment) ||
            (pt.Status == PaymentTransactionStatus.Matched && pt.ReasonCode == PaymentTransactionReason.LatePaymentAccepted);

        if (!isEligibleClassification)
        {
            return Error.Conflict("TRANSACTION_NOT_ELIGIBLE_FOR_ACCEPTANCE",
                $"Giao dịch ngân hàng đang ở trạng thái {pt.Status} ({pt.ReasonCode}), không đủ điều kiện chấp nhận thanh toán trễ.");
        }

        return Result.Success();
    }

    public static Result ValidateNoRefundReservation(int existingRefundCount)
    {
        if (existingRefundCount > 0)
        {
            return Error.Conflict("REFUND_ALREADY_RESERVED", "Khoản thanh toán đã có yêu cầu hoàn tiền; không thể chấp nhận thanh toán trễ.");
        }

        return Result.Success();
    }
}
