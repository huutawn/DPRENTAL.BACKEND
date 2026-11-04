using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

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

    // Nội dung chuyển khoản admin dùng khi trả payout (QR); webhook SePay tiền ra đọc mã này để tự xác nhận.
    [Required, MaxLength(14)]
    public string TransferCode { get; private set; } = default!;

    // Tài khoản nhà cung cấp chốt lúc quyết toán; số tài khoản được mã hóa khi lưu (PaymentServiceDbContext).
    [MaxLength(32)] public string? RecipientBankCode { get; private set; }
    [MaxLength(512)] public string? RecipientAccountNumber { get; private set; }
    [MaxLength(128)] public string? RecipientAccountHolderName { get; private set; }

    [Required, MaxLength(32)]
    public string Status { get; private set; } = default!;

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    [ForeignKey(nameof(SettlementId))]
    public RentalItemSettlementRecord? Settlement { get; private set; }

    private SupplierPayout() { }

    // Subtask này chỉ có thao tác tạo PENDING_PAYOUT.
    internal static SupplierPayout CreatePending(
        Guid settlementId, Guid rentalItemId, Guid supplierId,
        decimal amount, string currency, DateTimeOffset now, TransferRecipient? recipient = null)
    {
        if (settlementId == Guid.Empty || rentalItemId == Guid.Empty || supplierId == Guid.Empty)
            throw new ArgumentException("Settlement, rental item and supplier IDs are required.");
        if (amount <= 0 || decimal.Truncate(amount) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), "Payout amount must be a positive whole VND amount.");
        if (!string.Equals(currency?.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Currency must be VND.", nameof(currency));

        return new SupplierPayout
        {
            Id = Guid.NewGuid(),
            PayoutRef = BuildPayoutRef(settlementId),
            SettlementId = settlementId,
            RentalItemId = rentalItemId,
            SupplierId = supplierId,
            Amount = amount,
            Currency = "VND",
            TransferCode = PaymentCodeGenerator.Generate(PaymentCodeGenerator.PayoutPrefix),
            RecipientBankCode = recipient?.BankCode,
            RecipientAccountNumber = recipient?.AccountNumber,
            RecipientAccountHolderName = recipient?.AccountHolderName,
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

    public Error? ValidateCanComplete()
    {
        if (Status == StatusCompleted)
            return Error.Conflict("Payment.PayoutAlreadyCompleted", "Payout đã được xác nhận hoàn tất.");
        if (Status != StatusPendingPayout)
            return Error.Conflict("Payment.PayoutNotPending", "Payout không còn ở trạng thái PENDING_PAYOUT.");
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
