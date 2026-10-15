namespace Dprental.PaymentService.Domain.Repository;

public enum StorageConstraintViolationType
{
    OperationKey,
    RentalOrderId,
    PaymentCode,
    PaymentRef,
    PaymentTransaction,
    Other
}

public class StorageConstraintViolationException : Exception
{
    public StorageConstraintViolationType ViolationType { get; }
    public string? ConstraintName { get; }

    public StorageConstraintViolationException(
        StorageConstraintViolationType violationType,
        string? constraintName = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Storage constraint violation: {violationType} ({constraintName})", innerException)
    {
        ViolationType = violationType;
        ConstraintName = constraintName;
    }
}

public static class StorageConstraints
{
    public const string OperationKey = "IX_operation_receipts_OperationKey";
    public const string RentalOrderId = "IX_invoices_RentalOrderId";
    public const string PaymentCode = "IX_invoices_PaymentCode";
    public const string PaymentRef = "IX_invoices_PaymentRef";
    public const string InvoiceRef = "IX_invoices_InvoiceRef";
    public const string InvoiceItemUnique = "IX_invoice_items_InvoiceId_RentalItemId";
    public const string PaymentTransaction = "IX_payment_transactions_Provider_ProviderTransactionId";
}
