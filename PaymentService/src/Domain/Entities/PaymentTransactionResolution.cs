using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

/// <summary>Kết quả đối soát thủ công; bản ghi SePay gốc giữ nguyên.</summary>
[Table("payment_transaction_resolutions")]
public sealed class PaymentTransactionResolution
{
    [Key] public Guid Id { get; private set; }
    [Required] public Guid TransactionId { get; private set; }
    [Required] public Guid InvoiceId { get; private set; }
    [Required, MaxLength(64)] public string InvoiceRef { get; private set; } = default!;
    [Required, MaxLength(64)] public string OperationKey { get; private set; } = default!;
    [Required, MaxLength(64)] public string PayloadFingerprint { get; private set; } = default!;
    [Required, MaxLength(256)] public string ExternalReference { get; private set; } = default!;
    [Required, MaxLength(500)] public string AuditReason { get; private set; } = default!;
    [Required] public Guid ActorId { get; private set; }
    [Required] public DateTimeOffset OccurredAt { get; private set; }

    private PaymentTransactionResolution() { }

    public static PaymentTransactionResolution Create(
        PaymentTransaction transaction, Invoice invoice, Guid adminId, string operationKey,
        string fingerprint, string externalReference, string auditReason, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            InvoiceId = invoice.Id,
            InvoiceRef = invoice.InvoiceRef,
            OperationKey = operationKey,
            PayloadFingerprint = fingerprint,
            ExternalReference = externalReference,
            AuditReason = auditReason,
            ActorId = adminId,
            OccurredAt = now.ToUniversalTime()
        };
}
