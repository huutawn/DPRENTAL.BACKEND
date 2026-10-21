using System.Security.Cryptography;

namespace Dprental.PaymentService.Common.Cryptography;

public static class AesGcmEncryption
{
    public const int NonceSizeBytes = 12; // 96 bits standard for GCM
    public const int TagSizeBytes = 16;   // 128 bits standard for GCM
    public const int KeySizeBytes = 32;   // 256 bits AES

    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"AES-256 key must be exactly {KeySizeBytes} bytes.", nameof(key));
        }

        byte[] nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return (ciphertext, nonce, tag);
    }

    public static byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"AES-256 key must be exactly {KeySizeBytes} bytes.", nameof(key));
        }

        if (nonce.Length != NonceSizeBytes)
        {
            throw new ArgumentException($"Nonce must be {NonceSizeBytes} bytes.", nameof(nonce));
        }

        if (tag.Length != TagSizeBytes)
        {
            throw new ArgumentException($"Tag must be {TagSizeBytes} bytes.", nameof(tag));
        }

        byte[] plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return plaintext;
    }
}
