using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Events;

namespace Dprental.PaymentService.Domain.Entities;

public sealed class Invoice
{
    private readonly List<InvoiceItem> _items = [];
    private readonly List<IFinancialDomainEvent> _domainEvents = [];

    public Guid Id { get; private set; }
    public string InvoiceRef { get; private set; } = string.Empty;
    public Guid RentalOrderId { get; private set; }
    public string OrderCode { get; private set; } = string.Empty;
    public Guid RenterId { get; private set; }
    public string Currency { get; private set; } = "VND";
    public decimal TotalRentalAmount { get; private set; }
    public decimal TotalDepositAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string Status { get; private set; } = InvoiceStatus.Pending;
    public string PaymentCode { get; private set; } = string.Empty;
    public string MerchantBank { get; private set; } = string.Empty;
    public string MerchantAccount { get; private set; } = string.Empty;
    public DateTimeOffset PaymentDeadline { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset? ExpiredAt { get; private set; }
    public string? CancelReason { get; private set; }
    public string? PaymentRef { get; private set; }
    public long Revision { get; private set; } = 1;

    public IReadOnlyList<InvoiceItem> Items => _items.AsReadOnly();
    public IReadOnlyList<IFinancialDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();
    internal void AddDomainEvent(IFinancialDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

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

        foreach (var item in items)
        {
            item.AttachToInvoice(invoice.Id);
            invoice._items.Add(item);
        }

        if (totalAmount == 0m)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = utcCreated;
            invoice.PaymentRef = $"PAY-{Guid.NewGuid():N}";
            invoice.AddDomainEvent(new InvoicePaidDomainEvent(
                invoice.Id, invoice.InvoiceRef, invoice.RentalOrderId, invoice.PaymentRef, 0m, "VND", utcCreated, utcCreated));
        }
        else
        {
            if (utcDeadline <= utcCreated)
            {
                return Error.Validation("INVALID_PAYMENT_DEADLINE", "Hạn thanh toán phải lớn hơn thời điểm tạo mới.");
            }

            invoice.Status = InvoiceStatus.Pending;
        }

        invoice.AddDomainEvent(new InvoiceCreatedDomainEvent(
            invoice.Id,
            invoice.InvoiceRef,
            invoice.RentalOrderId,
            invoice.OrderCode,
            invoice.RenterId,
            invoice.TotalAmount,
            invoice.Currency,
            invoice.Status,
            invoice.PaymentDeadline,
            utcCreated));

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
        Revision++;

        AddDomainEvent(new InvoicePaidDomainEvent(
            Id, InvoiceRef, RentalOrderId, normalizedPaymentRef, TotalAmount, Currency, utcPaidAt, utcPaidAt));

        return Result.Success();
    }

    public Result RecordLatePayment(string paymentRef, DateTimeOffset paidAt, DateTimeOffset processedAt)
    {
        if (string.IsNullOrWhiteSpace(paymentRef))
        {
            return Error.Validation("Payment.InvalidPaymentRef", "PaymentRef không được rỗng.");
        }

        string normalizedPaymentRef = paymentRef.Trim();
        DateTimeOffset utcPaidAt = paidAt.ToUniversalTime();
        DateTimeOffset utcProcessedAt = processedAt.ToUniversalTime();

        if (Status == InvoiceStatus.Paid)
        {
            return Error.Conflict("INVOICE_ALREADY_PAID", "Hóa đơn đã được thanh toán, không thể ghi nhận thanh toán trễ.");
        }

        if (Status == InvoiceStatus.Pending)
        {
            Status = InvoiceStatus.Expired;
            ExpiredAt = utcProcessedAt;
            Revision++;
            AddDomainEvent(new InvoiceExpiredDomainEvent(Id, InvoiceRef, RentalOrderId, utcProcessedAt));
        }

        PaymentRef = normalizedPaymentRef;
        PaidAt = utcPaidAt;
        Revision++;

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

        DateTimeOffset utcCancelledAt = cancelledAt.ToUniversalTime();
        Status = InvoiceStatus.Cancelled;
        CancelledAt = utcCancelledAt;
        CancelReason = reason;
        Revision++;

        AddDomainEvent(new InvoiceCancelledDomainEvent(Id, InvoiceRef, RentalOrderId, reason, utcCancelledAt));

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

        DateTimeOffset utcExpiredAt = expiredAt.ToUniversalTime();
        Status = InvoiceStatus.Expired;
        ExpiredAt = utcExpiredAt;
        Revision++;

        AddDomainEvent(new InvoiceExpiredDomainEvent(Id, InvoiceRef, RentalOrderId, utcExpiredAt));

        return Result.Success();
    }
}
