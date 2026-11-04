using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

public static class FinancialOperationTypes
{
    public const string CompleteSupplierPayout = "CompleteSupplierPayout";
}

// Append-only: không setter công khai, không có phương thức sửa; DB chặn UPDATE/DELETE bằng trigger.
[Table("financial_operation_audits")]
public sealed class FinancialOperationAudit
{
    [Key] public Guid Id { get; private set; }

    [Required, MaxLength(64)] public string OperationKey { get; private set; } = default!;
    [Required, MaxLength(64)] public string OperationType { get; private set; } = default!;
    [Required, MaxLength(64)] public string PayloadFingerprint { get; private set; } = default!;

    [Required] public Guid PayoutId { get; private set; }
    [Required] public Guid SettlementId { get; private set; }
    [Required] public Guid SupplierId { get; private set; }
    [Required] public Guid ActorId { get; private set; }

    // Ảnh chụp trạng thái payout tại thời điểm thao tác.
    [Required, Column(TypeName = "numeric(18,0)")] public decimal Amount { get; private set; }
    [Required, MaxLength(3)] public string Currency { get; private set; } = default!;
    // Giữ cột Destination* cho bản ghi cũ; payout hiện không còn tài khoản đích nên bản ghi mới để null.
    [MaxLength(256)] public string? DestinationRef { get; private set; }
    [MaxLength(256)] public string? ExternalTransferReference { get; private set; }

    [Required, MaxLength(500)] public string Reason { get; private set; } = default!;
    [Required] public DateTimeOffset OccurredAt { get; private set; }
    public Guid? DestinationId { get; private set; }

    [MaxLength(256)] public string? PreviousDestinationRef { get; private set; }

    private FinancialOperationAudit() { }

    internal static FinancialOperationAudit Create(
        string operationKey, string operationType, string fingerprint,
        SupplierPayout payout, Guid actorId, string reason, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            OperationKey = operationKey,
            OperationType = operationType,
            PayloadFingerprint = fingerprint,
            PayoutId = payout.Id,
            SettlementId = payout.SettlementId,
            SupplierId = payout.SupplierId,
            ActorId = actorId,
            Amount = payout.Amount,
            Currency = payout.Currency,
            ExternalTransferReference = payout.ExternalTransferReference,
            Reason = reason,
            OccurredAt = now.ToUniversalTime()
        };
}
