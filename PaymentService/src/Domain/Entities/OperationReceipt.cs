using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

[Table("operation_receipts")]
public sealed class OperationReceipt
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(64)]
    public string OperationKey { get; private set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string OperationType { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string PayloadFingerprint { get; private set; } = string.Empty;

    [Required]
    public string ResultJson { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string InvoiceRef { get; private set; } = string.Empty;

    [Required]
    public Guid RentalOrderId { get; private set; }

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }
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
