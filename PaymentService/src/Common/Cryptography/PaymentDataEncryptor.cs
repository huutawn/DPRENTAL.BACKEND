using System.Security.Cryptography;

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
    private readonly Dictionary<string, byte[]> _keyRing = new(StringComparer.Ordinal);
    private readonly string _activeKeyId;

    public PaymentDataEncryptor(byte[] activeKey, string activeKeyId = "dpr-enc-2026-v1", IReadOnlyDictionary<string, byte[]>? oldKeys = null)
    {
        ArgumentNullException.ThrowIfNull(activeKey);
        if (activeKey.Length != 32)
        {
            throw new InvalidOperationException($"Khóa mã hóa AES-256 phải có đúng 32 bytes (256-bit), hiện tại là {activeKey.Length} bytes.");
        }

        if (string.IsNullOrWhiteSpace(activeKeyId))
        {
            throw new InvalidOperationException("KeyId của khóa mã hóa đang hoạt động không được để trống.");
        }

        _activeKeyId = activeKeyId.Trim();
        _keyRing[_activeKeyId] = activeKey.ToArray();

        if (oldKeys is not null)
        {
            foreach (var (kId, kBytes) in oldKeys)
            {
                if (kBytes.Length != 32)
                {
                    throw new InvalidOperationException($"Khóa cũ '{kId}' phải có đúng 32 bytes (256-bit).");
                }
                _keyRing[kId] = kBytes.ToArray();
            }
        }
    }

    public string ActiveKeyId => _activeKeyId;

    public (string CiphertextBase64, string NonceBase64, string TagBase64, string KeyId) Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        byte[] key = _keyRing[_activeKeyId];
        var (ciphertext, nonce, tag) = AesGcmEncryption.Encrypt(key, plaintext, associatedData);
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
        if (!_keyRing.TryGetValue(keyId, out var key))
        {
            throw new CryptographicException($"Không tìm thấy khóa mã hóa AES cho KeyId '{keyId}'.");
        }

        byte[] ciphertext = Convert.FromBase64String(ciphertextBase64);
        byte[] nonce = Convert.FromBase64String(nonceBase64);
        byte[] tag = Convert.FromBase64String(tagBase64);

        return AesGcmEncryption.Decrypt(key, ciphertext, nonce, tag, associatedData);
    }
}
