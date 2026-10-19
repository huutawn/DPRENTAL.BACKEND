using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Domain.Entities;

[Table("supplier_payouts")]
public sealed class SupplierPayout
{
    public const string StatusPendingPayout = "PENDING_PAYOUT";

    [Key]
    public Guid Id { get; private set; }

    [Required, MaxLength(64)]
    public string PayoutRef { get; private set; } = default!;

    [Required] public Guid SettlementId { get; private set; }
    [Required] public Guid RentalItemId { get; private set; }
    [Required] public Guid SupplierId { get; private set; }

    [Required, Column(TypeName = "numeric(18, 0)")]
    public decimal Amount { get; private set; }

    [Required, MaxLength(3)]
    public string Currency { get; private set; } = default!;

    // Chờ Payment-owned destination đã xác minh: null thì payout không được hoàn tất.
    [MaxLength(256)]
    public string? DestinationRef { get; private set; }

    [Required, MaxLength(32)]
    public string Status { get; private set; } = default!;

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? DestinationId { get; private set; }
    public SupplierPayoutDestination? Destination { get; private set; }

    [ForeignKey(nameof(SettlementId))]
    public RentalItemSettlementRecord? Settlement { get; private set; }

    private SupplierPayout() { }

    // Subtask này chỉ có thao tác tạo PENDING_PAYOUT.
    internal static SupplierPayout CreatePending(
        Guid settlementId, Guid rentalItemId, Guid supplierId,
        decimal amount, string currency, string? destinationRef, DateTimeOffset now)
    {
        if (settlementId == Guid.Empty || rentalItemId == Guid.Empty || supplierId == Guid.Empty)
            throw new ArgumentException("Settlement, rental item and supplier IDs are required.");
        if (amount <= 0 || decimal.Truncate(amount) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), "Payout amount must be a positive whole VND amount.");
        if (!string.Equals(currency?.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Currency must be VND.", nameof(currency));
        if (destinationRef is not null && destinationRef.Trim().Length > 256)
            throw new ArgumentOutOfRangeException(nameof(destinationRef), "Destination reference is too long.");

        return new SupplierPayout
        {
            Id = Guid.NewGuid(),
            PayoutRef = BuildPayoutRef(settlementId),
            SettlementId = settlementId,
            RentalItemId = rentalItemId,
            SupplierId = supplierId,
            Amount = amount,
            Currency = "VND",
            DestinationRef = string.IsNullOrWhiteSpace(destinationRef) ? null : destinationRef.Trim(),
            Status = StatusPendingPayout,
            CreatedAt = now.ToUniversalTime()
        };
    }

    // Cùng quy ước với SettlementRef (STL-{id:N}); xác định từ SettlementId nên backfill/retry ra cùng giá trị.
    public static string BuildPayoutRef(Guid settlementId) => $"PO-{settlementId:N}";


    public const string StatusCompleted = "COMPLETED";
    [MaxLength(256)]
    public string? ExternalTransferReference { get; private set; }
    public Guid? CompletedBy { get; private set; }
    public Error? ValidateCanAttachDestination()
    {
        if (Status != StatusPendingPayout)
            return Error.Conflict("Payment.PayoutNotPending", "Payout không còn ở trạng thái PENDING_PAYOUT.");
        if (!string.IsNullOrWhiteSpace(DestinationRef))
            return Error.Conflict("Payment.PayoutDestinationAlreadySet", "Payout đã có DestinationRef.");
        return null;
    }
    internal void AttachDestination(string destinationRef)
    {
        if (ValidateCanAttachDestination() is not null || string.IsNullOrWhiteSpace(destinationRef))
            throw new InvalidOperationException("Payout cannot accept a destination.");

        DestinationRef = destinationRef.Trim();
    }

    public Error? ValidateCanAttachDestination(Guid destinationId)
    {
        if (Status != StatusPendingPayout)
            return Error.Conflict("Payment.PayoutNotPending", "Payout không còn ở trạng thái PENDING_PAYOUT.");
        if (DestinationId == destinationId)
            return Error.Conflict("Payment.PayoutDestinationAlreadyAttached", "Payout đã gắn đúng payout destination này.");
        return null;
    }

    // Cho phép gắn lại khi payout còn chờ: payout cũ (DestinationRef chuỗi) và phiên bản cũ đều đi qua đường này, có audit.
    public void AttachDestination(SupplierPayoutDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.SupplierId != SupplierId)
            throw new InvalidOperationException("Destination belongs to a different supplier.");
        if (ValidateCanAttachDestination(destination.Id) is not null)
            throw new InvalidOperationException("Payout cannot accept this destination.");

        DestinationId = destination.Id;
        DestinationRef = destination.DestinationRef;
    }

    public Error? ValidateCanComplete()
    {
        if (Status == StatusCompleted)
            return Error.Conflict("Payment.PayoutAlreadyCompleted", "Payout đã được xác nhận hoàn tất.");
        if (Status != StatusPendingPayout)
            return Error.Conflict("Payment.PayoutNotPending", "Payout không còn ở trạng thái PENDING_PAYOUT.");
        if (DestinationId is null)
            return Error.Conflict("Payment.PayoutDestinationMissing",
                "Payout chưa gắn tài khoản nhận tiền đã xác minh nên chưa thể xác nhận chuyển tiền.");
        return null;
    }

    internal void Complete(string externalTransferReference, Guid completedBy, DateTimeOffset now)
    {
        if (ValidateCanComplete() is not null)
            throw new InvalidOperationException("Payout cannot be completed in its current state.");
        if (string.IsNullOrWhiteSpace(externalTransferReference))
            throw new ArgumentException("External transfer reference is required.", nameof(externalTransferReference));
        if (completedBy == Guid.Empty)
            throw new ArgumentException("Admin id is required.", nameof(completedBy));

        Status = StatusCompleted;
        ExternalTransferReference = externalTransferReference.Trim();
        CompletedBy = completedBy;
        CompletedAt = now.ToUniversalTime();
    }
}
