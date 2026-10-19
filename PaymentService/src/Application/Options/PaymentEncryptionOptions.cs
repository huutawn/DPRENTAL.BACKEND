namespace Dprental.PaymentService.Application.Options;

public sealed class PaymentEncryptionOptions
{
    public const string SectionName = "PaymentEncryption";

    public string ActiveKeyId { get; set; } = "dpr-enc-2026-v1";

    public string? KeyFilePath { get; set; }

    public string? DataEncryptionKeyBase64 { get; set; }

    public byte[] GetKeyBytes()
    {
        if (!string.IsNullOrWhiteSpace(KeyFilePath) && File.Exists(KeyFilePath))
        {
            string content = File.ReadAllText(KeyFilePath).Trim();
            return Convert.FromBase64String(content);
        }

        if (!string.IsNullOrWhiteSpace(DataEncryptionKeyBase64))
        {
            return Convert.FromBase64String(DataEncryptionKeyBase64.Trim());
        }

        throw new InvalidOperationException("Thiếu khóa mã hóa AES-256-GCM: Hãy cấu hình PaymentEncryption:DataEncryptionKeyBase64 hoặc KeyFilePath.");
    }
}
