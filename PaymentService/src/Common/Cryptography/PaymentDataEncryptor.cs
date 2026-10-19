using System.Text;

namespace Dprental.PaymentService.Common.Cryptography;

public interface IPaymentDataEncryptor
{
    string ActiveKeyId { get; }

    (string CiphertextBase64, string NonceBase64, string TagBase64, string KeyId) Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData);

    byte[] Decrypt(
        string ciphertextBase64,
        string nonceBase64,
        string tagBase64,
        string keyId,
        ReadOnlySpan<byte> associatedData);
}

public sealed class PaymentDataEncryptor : IPaymentDataEncryptor
{
    private readonly byte[] _key;
    private readonly string _activeKeyId;

    public PaymentDataEncryptor(byte[] key, string activeKeyId = "dpr-enc-2026-v1")
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new InvalidOperationException($"Khóa mã hóa AES-256 phải có đúng 32 bytes (256-bit), hiện tại là {key.Length} bytes.");
        }

        _key = key.ToArray();
        _activeKeyId = !string.IsNullOrWhiteSpace(activeKeyId) ? activeKeyId : "dpr-enc-2026-v1";
    }

    public string ActiveKeyId => _activeKeyId;

    public (string CiphertextBase64, string NonceBase64, string TagBase64, string KeyId) Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        var (ciphertext, nonce, tag) = AesGcmEncryption.Encrypt(_key, plaintext, associatedData);
        return (
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            _activeKeyId);
    }

    public byte[] Decrypt(
        string ciphertextBase64,
        string nonceBase64,
        string tagBase64,
        string keyId,
        ReadOnlySpan<byte> associatedData)
    {
        byte[] ciphertext = Convert.FromBase64String(ciphertextBase64);
        byte[] nonce = Convert.FromBase64String(nonceBase64);
        byte[] tag = Convert.FromBase64String(tagBase64);

        return AesGcmEncryption.Decrypt(_key, ciphertext, nonce, tag, associatedData);
    }
}
