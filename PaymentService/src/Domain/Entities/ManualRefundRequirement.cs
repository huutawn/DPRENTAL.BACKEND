using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Domain.Entities;

public enum ManualRefundStatus
{
    PendingManualRefund = 1,
    Completed = 2
}

[Table("manual_refund_requirements")]
public sealed class ManualRefundRequirement
{
    [Key]
    public Guid Id { get; private set; }

    [Required, MaxLength(64)]
    public string RequirementRef { get; private set; } = default!;

    [Required] public Guid SettlementId { get; private set; }
    [Required] public Guid InvoiceId { get; private set; }
    [Required] public Guid RentalItemId { get; private set; }
    [Required] public Guid RenterId { get; private set; }

    [Required, MaxLength(64)]
    public string PaymentRef { get; private set; } = default!;

    [Required, MaxLength(256)]
    public string Reason { get; private set; } = default!;

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal Amount { get; private set; }

    [Required]
    public ManualRefundStatus Status { get; private set; }

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    [MaxLength(256)]
    public string? ExternalTransferReference { get; private set; }

    public Guid? CompletedBy { get; private set; }

    [ForeignKey(nameof(SettlementId))]
    public RentalItemSettlementRecord? Settlement { get; private set; }

    private ManualRefundRequirement() { }

    internal static ManualRefundRequirement CreatePending(
        Guid settlementId, Guid invoiceId, Guid rentalItemId, Guid renterId,
        string paymentRef, decimal amount, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        return new ManualRefundRequirement
        {
            Id = id,
            RequirementRef = $"MRF-{id:N}",
            SettlementId = settlementId,
            InvoiceId = invoiceId,
            RentalItemId = rentalItemId,
            RenterId = renterId,
            PaymentRef = paymentRef,
            Reason = "RENTAL_DEPOSIT_REFUND",
            Amount = amount,
            Status = ManualRefundStatus.PendingManualRefund,
            CreatedAt = now
        };
    }

    public Result<bool> Complete(string externalTransferReference, Guid completedBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalTransferReference) ||
            externalTransferReference.Trim().Length > 256 || completedBy == Guid.Empty)
            return Error.Validation("Payment.InvalidRefundConfirmation", "Mã chuyển khoản và người xác nhận phải hợp lệ.");

        var reference = externalTransferReference.Trim();
        if (Status == ManualRefundStatus.Completed)
        {
            return string.Equals(ExternalTransferReference, reference, StringComparison.Ordinal)
                ? Result.Success(false)
                : Error.Conflict("Payment.RefundAlreadyCompleted", "Yêu cầu hoàn cọc đã được xác nhận bằng mã chuyển khoản khác.");
        }

        Status = ManualRefundStatus.Completed;
        ExternalTransferReference = reference;
        CompletedBy = completedBy;
        CompletedAt = now.ToUniversalTime();
        return Result.Success(true);
    }
}
