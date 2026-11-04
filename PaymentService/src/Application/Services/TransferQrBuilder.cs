using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Application.Services;

/// <summary>Giải mã tài khoản người nhận đã chốt và ghép QR VietQR cho khoản tiền Admin cần chuyển ra.</summary>
internal static class TransferQrBuilder
{
    // entityType/recordId phải khớp lúc mã hóa trong PaymentServiceDbContext.EncryptRecipientAccount.
    public static (string? AccountNumber, string? QrUrl) Build(IPaymentDataEncryptor encryptor, string entityType, Guid recordId,
        string? bankCode, string? storedAccountNumber, decimal amount, string transferCode, bool isPending)
    {
        string? account = SensitiveFieldEncryptor.Decrypt(
            encryptor, entityType, recordId.ToString("D"), "RecipientAccountNumber", storedAccountNumber);
        // Khoản đã xong thì không trả QR để Admin không quét lại và chuyển hai lần.
        string? qrUrl = isPending && !string.IsNullOrWhiteSpace(bankCode) && !string.IsNullOrWhiteSpace(account)
            ? VietQrUrlGenerator.Generate(bankCode, account, amount, transferCode)
            : null;
        return (account, qrUrl);
    }
}
