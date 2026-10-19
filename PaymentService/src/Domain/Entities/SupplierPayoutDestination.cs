using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

// Mỗi dòng là MỘT PHIÊN BẢN bất biến (DB chặn UPDATE/DELETE). Đổi tài khoản = tạo phiên bản mới.
// Không lưu số tài khoản đầy đủ: chỉ 4 số cuối để hiển thị và dấu vân tay HMAC để phát hiện trùng.
[Table("supplier_payout_destinations")]
public sealed class SupplierPayoutDestination
{
    [Key] public Guid Id { get; private set; }
    [Required] public Guid SupplierId { get; private set; }

    [Required, MaxLength(64)] public string DestinationRef { get; private set; } = default!;
    [Required] public int Version { get; private set; }

    [Required, MaxLength(32)] public string BankCode { get; private set; } = default!;
    [Required, MaxLength(128)] public string AccountHolderName { get; private set; } = default!;
    [Required, MaxLength(4)] public string AccountNumberLast4 { get; private set; } = default!;
    [Required, MaxLength(64)] public string AccountFingerprint { get; private set; } = default!;

    [Required, MaxLength(128)] public string VerificationRef { get; private set; } = default!;
    [Required] public DateTimeOffset VerifiedAt { get; private set; }
    [Required] public Guid VerifiedBy { get; private set; }

    // Idempotency + audit của việc đăng ký nằm ngay trên phiên bản (bất biến).
    [Required, MaxLength(64)] public string OperationKey { get; private set; } = default!;
    [Required, MaxLength(64)] public string PayloadFingerprint { get; private set; } = default!;
    [Required, MaxLength(500)] public string AuditReason { get; private set; } = default!;
    [Required] public DateTimeOffset CreatedAt { get; private set; }

    [NotMapped] public string AccountNumberMasked => $"****{AccountNumberLast4}";

    private SupplierPayoutDestination() { }

    internal static SupplierPayoutDestination Create(
        Guid supplierId, string destinationRef, int version, string bankCode, string accountHolderName,
        string accountNumberLast4, string accountFingerprint, string verificationRef,
        Guid verifiedBy, string operationKey, string payloadFingerprint, string auditReason,
        DateTimeOffset now)
    {
        if (supplierId == Guid.Empty) throw new ArgumentException("SupplierId is required.", nameof(supplierId));
        if (verifiedBy == Guid.Empty) throw new ArgumentException("VerifiedBy is required.", nameof(verifiedBy));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (accountNumberLast4 is not { Length: 4 } || !accountNumberLast4.All(char.IsAsciiDigit))
            throw new ArgumentException("Last 4 digits are required.", nameof(accountNumberLast4));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(bankCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountHolderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReason);

        var utc = now.ToUniversalTime();
        return new SupplierPayoutDestination
        {
            Id = Guid.NewGuid(),
            SupplierId = supplierId,
            DestinationRef = destinationRef,
            Version = version,
            BankCode = bankCode,
            AccountHolderName = accountHolderName,
            AccountNumberLast4 = accountNumberLast4,
            AccountFingerprint = accountFingerprint,
            VerificationRef = verificationRef,
            VerifiedAt = utc,
            VerifiedBy = verifiedBy,
            OperationKey = operationKey,
            PayloadFingerprint = payloadFingerprint,
            AuditReason = auditReason,
            CreatedAt = utc
        };
    }
}
