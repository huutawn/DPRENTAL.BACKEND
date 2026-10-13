using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Domain.Entities;

[Table("invoices")]
public sealed class Invoice
{
    private readonly List<InvoiceItem> _items = [];

    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(64)]
    public string InvoiceRef { get; private set; } = string.Empty;

    [Required]
    public Guid RentalOrderId { get; private set; }

    [Required]
    [MaxLength(64)]
    public string OrderCode { get; private set; } = string.Empty;

    [Required]
    public Guid RenterId { get; private set; }

    [Required]
    [MaxLength(3)]
    public string Currency { get; private set; } = "VND";

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal TotalRentalAmount { get; private set; }

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal TotalDepositAmount { get; private set; }

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal TotalAmount { get; private set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; private set; } = InvoiceStatus.Pending;

    [Required]
    [MaxLength(32)]
    public string PaymentCode { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string MerchantBank { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string MerchantAccount { get; private set; } = string.Empty;

    [Required]
    public DateTimeOffset PaymentDeadline { get; private set; }

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? ExpiredAt { get; private set; }

    [MaxLength(500)]
    public string? CancelReason { get; private set; }

    [MaxLength(64)]
    public string? PaymentRef { get; private set; }

    [InverseProperty(nameof(InvoiceItem.Invoice))]
    public IReadOnlyList<InvoiceItem> Items => _items.AsReadOnly();
    private Invoice()
    {
    }

    public static Result<Invoice> Create(
        Guid rentalOrderId,
        string orderCode,
        Guid renterId,
        string currency,
        DateTimeOffset paymentDeadline,
        IReadOnlyList<InvoiceItem> items,
        string paymentCode,
        string merchantBank,
        string merchantAccount,
        DateTimeOffset utcNow,
        string? invoiceRef = null)
    {
        if (rentalOrderId == Guid.Empty || renterId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId và RenterId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(orderCode) || orderCode.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOrderCode", "OrderCode không được rỗng và tối đa 64 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(currency) || !string.Equals(currency.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Payment.InvalidCurrency", "Currency bắt buộc phải là 'VND'.");
        }

        if (string.IsNullOrWhiteSpace(paymentCode))
        {
            return Error.Validation("Payment.InvalidPaymentCode", "PaymentCode không được rỗng.");
        }

        if (string.IsNullOrWhiteSpace(merchantBank) || string.IsNullOrWhiteSpace(merchantAccount))
        {
            return Error.Validation("Payment.InvalidMerchant", "Thông tin tài khoản ngân hàng của merchant không được rỗng.");
        }

        if (items is null || items.Count == 0)
        {
            return Error.Validation("Payment.EmptyItems", "Danh sách items trong hóa đơn không được rỗng.");
        }

        if (items.Any(i => i is null))
        {
            return Error.Validation("Payment.NullItem", "Danh sách items trong hóa đơn không được chứa phần tử null.");
        }

        if (items.Select(i => i.RentalItemId).Distinct().Count() != items.Count)
        {
            return Error.Conflict("Payment.DuplicateItemId", "Danh sách items chứa RentalItemId bị trùng lặp.");
        }

        decimal totalRental = items.Sum(i => i.RentalAmount);
        var rentalCheck = VndAmountValidator.ValidateVndAmount(totalRental, "Tổng tiền thuê");
        if (rentalCheck.IsFailure) return rentalCheck.Error;

        decimal totalDeposit = items.Sum(i => i.DepositAmount);
        var depositCheck = VndAmountValidator.ValidateVndAmount(totalDeposit, "Tổng tiền cọc");
        if (depositCheck.IsFailure) return depositCheck.Error;

        decimal totalAmount = totalRental + totalDeposit;
        var totalCheck = VndAmountValidator.ValidateVndAmount(totalAmount, "Tổng tiền hóa đơn");
        if (totalCheck.IsFailure) return totalCheck.Error;

        string finalInvoiceRef = string.IsNullOrWhiteSpace(invoiceRef)
            ? $"INV-{Guid.NewGuid():N}"
            : invoiceRef;

        DateTimeOffset utcDeadline = paymentDeadline.ToUniversalTime();
        DateTimeOffset utcCreated = utcNow.ToUniversalTime();

        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            InvoiceRef = finalInvoiceRef,
            RentalOrderId = rentalOrderId,
            OrderCode = orderCode.Trim(),
            RenterId = renterId,
            Currency = "VND",
            TotalRentalAmount = totalRental,
            TotalDepositAmount = totalDeposit,
            TotalAmount = totalAmount,
            PaymentCode = paymentCode,
            MerchantBank = merchantBank.Trim(),
            MerchantAccount = merchantAccount.Trim(),
            PaymentDeadline = utcDeadline,
            CreatedAt = utcCreated
        };

        invoice._items.AddRange(items);

        if (totalAmount == 0m)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = utcCreated;
            invoice.PaymentRef = $"PAY-{Guid.NewGuid():N}";
        }
        else
        {
            if (utcDeadline <= utcCreated)
            {
                return Error.Validation("INVALID_PAYMENT_DEADLINE", "Hạn thanh toán phải lớn hơn thời điểm tạo mới.");
            }

            invoice.Status = InvoiceStatus.Pending;
        }

        return Result.Success(invoice);
    }

    public Result MarkPaid(string paymentRef, decimal amount, string currency, DateTimeOffset paidAt)
    {
        if (string.IsNullOrWhiteSpace(paymentRef))
        {
            return Error.Validation("Payment.InvalidPaymentRef", "PaymentRef không được rỗng.");
        }

        string normalizedPaymentRef = paymentRef.Trim();
        DateTimeOffset utcPaidAt = paidAt.ToUniversalTime();

        if (Status == InvoiceStatus.Paid)
        {
            if (string.Equals(PaymentRef, normalizedPaymentRef, StringComparison.Ordinal)
                && TotalAmount == amount
                && string.Equals(Currency, currency?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success();
            }

            return Error.Conflict("INVOICE_ALREADY_PAID", "Hóa đơn đã được thanh toán với thông tin khác.");
        }

        if (Status == InvoiceStatus.Expired)
        {
            return Error.Conflict("INVOICE_EXPIRED", "Hóa đơn đã hết hạn, không thể thanh toán.");
        }

        if (Status == InvoiceStatus.Cancelled)
        {
            return Error.Conflict("INVOICE_CANCELLED", "Hóa đơn đã bị hủy, không thể thanh toán.");
        }

        if (!string.Equals(Currency, currency?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("CURRENCY_MISMATCH", $"Tiền tệ thanh toán ({currency}) không khớp với hóa đơn ({Currency}).");
        }

        if (TotalAmount != amount)
        {
            return Error.Validation("TOTAL_AMOUNT_MISMATCH", $"Số tiền thanh toán ({amount}) không khớp với tổng tiền hóa đơn ({TotalAmount}).");
        }

        if (utcPaidAt >= PaymentDeadline)
        {
            return Error.Conflict("INVOICE_EXPIRED", "Thời điểm thanh toán đã quá hạn thanh toán của hóa đơn.");
        }

        Status = InvoiceStatus.Paid;
        PaymentRef = normalizedPaymentRef;
        PaidAt = utcPaidAt;

        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset cancelledAt)
    {
        if (Status == InvoiceStatus.Paid)
        {
            return Error.Conflict("INVOICE_ALREADY_PAID", "Hóa đơn đã được thanh toán, không thể hủy.");
        }

        if (Status == InvoiceStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status == InvoiceStatus.Expired)
        {
            // Invoice is already expired; confirmation acknowledges closure without overwriting status
            return Result.Success();
        }

        Status = InvoiceStatus.Cancelled;
        CancelledAt = cancelledAt.ToUniversalTime();
        CancelReason = reason;

        return Result.Success();
    }

    public Result Expire(DateTimeOffset expiredAt)
    {
        if (Status == InvoiceStatus.Paid)
        {
            return Error.Conflict("INVOICE_ALREADY_PAID", "Không thể expire hóa đơn đã thanh toán.");
        }

        if (Status == InvoiceStatus.Cancelled || Status == InvoiceStatus.Expired)
        {
            return Result.Success();
        }

        Status = InvoiceStatus.Expired;
        ExpiredAt = expiredAt.ToUniversalTime();

        return Result.Success();
    }
}
