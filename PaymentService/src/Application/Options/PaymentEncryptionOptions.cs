namespace Dprental.PaymentService.Application.Options;

public sealed class PaymentEncryptionOptions
{
    public const string SectionName = "PaymentEncryption";

    public string ActiveKeyId { get; set; } = "dpr-enc-2026-v1";

    public string? KeyFilePath { get; set; }

    public string? DataEncryptionKeyBase64 { get; set; }

    public Dictionary<string, string> OldKeys { get; set; } = new(StringComparer.Ordinal);

    public byte[] GetKeyBytes()
    {
        if (!string.IsNullOrWhiteSpace(KeyFilePath))
        {
            if (!File.Exists(KeyFilePath))
            {
                throw new InvalidOperationException($"Không tìm thấy file secret khóa mã hóa tại '{KeyFilePath}'. Startup bị dừng; không được tự ý fallback sang cấu hình khác.");
            }

            try
            {
                string content = File.ReadAllText(KeyFilePath).Trim();
                return Convert.FromBase64String(content);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Lỗi đọc file secret khóa mã hóa tại '{KeyFilePath}': {ex.Message}. Startup bị dừng.", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(DataEncryptionKeyBase64))
        {
            return Convert.FromBase64String(DataEncryptionKeyBase64.Trim());
        }

        throw new InvalidOperationException("Thiếu khóa mã hóa AES-256-GCM: Hãy cấu hình PaymentEncryption:DataEncryptionKeyBase64 hoặc KeyFilePath.");
    }

    public IReadOnlyDictionary<string, byte[]> GetOldKeyBytes()
    {
        var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (kId, b64) in OldKeys)
        {
            if (!string.IsNullOrWhiteSpace(b64))
            {
                dict[kId] = Convert.FromBase64String(b64.Trim());
            }
        }
        return dict;
    }
}
