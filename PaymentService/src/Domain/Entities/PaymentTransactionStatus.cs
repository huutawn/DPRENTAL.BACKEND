namespace Dprental.PaymentService.Domain.Entities;

public static class PaymentTransactionStatus
{
    public const string Matched = "MATCHED";
    public const string Unmatched = "UNMATCHED";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string ManualRefundRequired = "MANUAL_REFUND_REQUIRED";
}

public static class PaymentTransactionReason
{
    public const string ExactMatch = "EXACT_MATCH";
    public const string MissingPaymentCode = "MISSING_PAYMENT_CODE";
    public const string AmbiguousPaymentCode = "AMBIGUOUS_PAYMENT_CODE";
    public const string InvoiceNotFound = "INVOICE_NOT_FOUND";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string AccountMismatch = "ACCOUNT_MISMATCH";
    public const string OutgoingTransfer = "OUTGOING_TRANSFER";
    public const string InvoiceAlreadyPaid = "INVOICE_ALREADY_PAID";
    public const string LatePayment = "LATE_PAYMENT";
    public const string LatePaymentAccepted = "LATE_PAYMENT_ACCEPTED";
}
