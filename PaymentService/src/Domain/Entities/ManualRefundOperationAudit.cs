using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

public static class ManualRefundOperationTypes
{
    public const string CompleteManualRefund = "CompleteManualRefund";
}

[Table("manual_refund_operation_audits")]
public sealed class ManualRefundOperationAudit
{
    [Key] public Guid Id { get; private set; }

    [Required, MaxLength(64)] public string OperationKey { get; private set; } = default!;
    [Required, MaxLength(32)] public string OperationType { get; private set; } = default!;
    [Required, MaxLength(64)] public string PayloadFingerprint { get; private set; } = default!;

    [Required] public Guid RefundRequirementId { get; private set; }
    public Guid? SettlementId { get; private set; }
    public Guid? RentalItemId { get; private set; }
    [Required] public Guid ActorId { get; private set; }

    [Required, Column(TypeName = "numeric(18,0)")] public decimal Amount { get; private set; }
    [Required, MaxLength(256)] public string ExternalTransferReference { get; private set; } = default!;
    [Required, MaxLength(500)] public string Reason { get; private set; } = default!;
    [Required] public DateTimeOffset OccurredAt { get; private set; }

    private ManualRefundOperationAudit() { }

    internal static ManualRefundOperationAudit Create(
        string operationKey, string fingerprint, ManualRefundRequirement refund,
        Guid actorId, string externalTransferReference, string reason, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            OperationKey = operationKey,
            OperationType = ManualRefundOperationTypes.CompleteManualRefund,
            PayloadFingerprint = fingerprint,
            RefundRequirementId = refund.Id,
            SettlementId = refund.SettlementId,
            RentalItemId = refund.RentalItemId,
            ActorId = actorId,
            Amount = refund.Amount,
            ExternalTransferReference = externalTransferReference,
            Reason = reason,
            OccurredAt = now.ToUniversalTime()
        };
}
