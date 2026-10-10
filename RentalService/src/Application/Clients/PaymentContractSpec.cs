namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Frozen specification metadata and wire constants for Rental–Payment Contract v1.
/// NOTE: Because PaymentService source code is managed by a separate team and not hosted in this repository,
/// routes and scopes are explicitly designated as PROPOSED for provider review and sign-off.
/// </summary>
public static class PaymentContractSpec
{
    public const string Version = "v1";
    public const string SourceSystem = "rental";
    public const string Status = "PROPOSED";
    public const string TimeZoneId = "Asia/Ho_Chi_Minh";

    public static class ProposedRoutes
    {
        public const string CreateInvoice = "/api/v1/invoices";
        public static string CancelInvoice(string invoiceRef) => $"/api/v1/invoices/{invoiceRef}/cancel";
        public const string RequestRefund = "/api/v1/refunds";
        public const string SettleRental = "/api/v1/settlements";
    }

    public static class ProposedScopes
    {
        public const string WriteInvoice = "payment.invoice.write";
        public const string WriteRefund = "payment.refund.write";
        public const string WriteSettlement = "payment.settlement.write";
    }

    public static class ErrorCodes
    {
        public const string InvoiceAlreadyPaid = "INVOICE_ALREADY_PAID";
        public const string InvoiceNotFound = "INVOICE_NOT_FOUND";
        public const string InvalidPaymentDeadline = "INVALID_PAYMENT_DEADLINE";
        public const string TotalAmountMismatch = "TOTAL_AMOUNT_MISMATCH";
        public const string DuplicateOperationKey = "DUPLICATE_OPERATION_KEY";
        public const string RefundExceedsPaidAmount = "REFUND_EXCEEDS_PAID_AMOUNT";
        public const string SettlementExceedsDeposit = "SETTLEMENT_EXCEEDS_DEPOSIT";
        public const string DuplicateSettlement = "DUPLICATE_SETTLEMENT";
        public const string CurrencyMismatch = "CURRENCY_MISMATCH";
        public const string OrderNotFound = "ORDER_NOT_FOUND";
    }

    public static class EventTypes
    {
        public const string InvoicePaid = "InvoicePaid";
        public const string RefundCompleted = "RefundCompleted";
        public const string RentalSettled = "RentalSettled";
    }
}
