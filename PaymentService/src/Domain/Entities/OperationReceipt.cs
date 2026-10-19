namespace Dprental.PaymentService.Domain.Entities;

public sealed class OperationReceipt
{
    public Guid Id { get; private set; }
    public string OperationKey { get; private set; } = string.Empty;
    public string OperationType { get; private set; } = string.Empty;
    public string PayloadFingerprint { get; private set; } = string.Empty;
    public string ResultJson { get; private set; } = string.Empty;
    public string InvoiceRef { get; private set; } = string.Empty;
    public Guid RentalOrderId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public long Revision { get; private set; } = 1;

    private OperationReceipt()
    {
    }

    public static OperationReceipt Create(
        string operationKey,
        string operationType,
        string payloadFingerprint,
        string resultJson,
        string invoiceRef,
        Guid rentalOrderId,
        DateTimeOffset createdAt)
    {
        return new OperationReceipt
        {
            Id = Guid.NewGuid(),
            OperationKey = operationKey,
            OperationType = operationType,
            PayloadFingerprint = payloadFingerprint,
            ResultJson = resultJson,
            InvoiceRef = invoiceRef,
            RentalOrderId = rentalOrderId,
            CreatedAt = createdAt.ToUniversalTime()
        };
    }
}
