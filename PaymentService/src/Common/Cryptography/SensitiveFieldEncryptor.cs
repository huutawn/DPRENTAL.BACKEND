using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.PaymentService.Common.Cryptography;

public sealed record EncryptedFieldEnvelope(
    string Ciphertext,
    string Nonce,
    string Tag,
    string KeyId);

public static class SensitiveFieldEncryptor
{
    public const string EnvelopePrefix = "ENC:v1:";

    public static string Encrypt(IPaymentDataEncryptor encryptor, string entityType, string recordId, string fieldName, string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        ArgumentNullException.ThrowIfNull(encryptor);
        byte[] aad = Encoding.UTF8.GetBytes($"{entityType.Trim()}:{recordId.Trim()}:{fieldName.Trim()}");
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        var (ciphertext, nonce, tag, keyId) = encryptor.Encrypt(plaintextBytes, aad);
        var env = new EncryptedFieldEnvelope(ciphertext, nonce, tag, keyId);
        return EnvelopePrefix + JsonSerializer.Serialize(env);
    }

    public static string? Decrypt(IPaymentDataEncryptor encryptor, string entityType, string recordId, string fieldName, string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return storedValue;
        }

        if (!storedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            return storedValue;
        }

        ArgumentNullException.ThrowIfNull(encryptor);
        string json = storedValue[EnvelopePrefix.Length..];
        var envelope = JsonSerializer.Deserialize<EncryptedFieldEnvelope>(json)
            ?? throw new CryptographicException("Malformed encrypted field envelope.");

        byte[] aad = Encoding.UTF8.GetBytes($"{entityType.Trim()}:{recordId.Trim()}:{fieldName.Trim()}");
        byte[] decryptedBytes = encryptor.Decrypt(
            envelope.Ciphertext,
            envelope.Nonce,
            envelope.Tag,
            envelope.KeyId,
            aad);

        return Encoding.UTF8.GetString(decryptedBytes);
    }

    public static bool IsEncrypted(string? storedValue)
    {
        return !string.IsNullOrWhiteSpace(storedValue) && storedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal);
    }
}
