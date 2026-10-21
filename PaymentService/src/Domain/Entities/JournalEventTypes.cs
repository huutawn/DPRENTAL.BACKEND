namespace Dprental.PaymentService.Domain.Entities;

public static class JournalEventTypes
{
    public const string InvoiceCreated = "INVOICE_CREATED";
    public const string InvoiceCancelled = "INVOICE_CANCELLED";
    public const string InvoiceExpired = "INVOICE_EXPIRED";
    public const string BankIncoming = "BANK_INCOMING";
    public const string BankOutgoing = "BANK_OUTGOING";
    public const string PaymentMatched = "PAYMENT_MATCHED";
    public const string PaymentUnmatched = "PAYMENT_UNMATCHED";
    public const string RentalSettled = "RENTAL_SETTLED";
    public const string RefundRequired = "REFUND_REQUIRED";
    public const string RefundConfirmed = "REFUND_CONFIRMED";
    public const string LegacyBaseline = "LEGACY_BASELINE";
}

public static class JournalConfirmationSources
{
    public const string Webhook = "WEBHOOK";
    public const string Admin = "ADMIN";
    public const string System = "SYSTEM";
    public const string LegacyBaseline = "LEGACY_BASELINE";
}

public static class JournalStreamTypes
{
    public const string Invoice = "Invoice";
    public const string PaymentTransaction = "PaymentTransaction";
    public const string RentalSettlement = "RentalSettlement";
    public const string ManualRefund = "ManualRefund";
}
