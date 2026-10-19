using System.Security.Cryptography;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.IntegrityHost.Options;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.IntegrityHost.Services;

public sealed class IntegrityKeyManager : IDisposable
{
    private readonly ECDsa _privateKey;
    private readonly string _keyId;
    private readonly string _publicKeyBase64;

    public IntegrityKeyManager(IOptions<IntegrityHostOptions> options)
    {
        var config = options?.Value ?? new IntegrityHostOptions();
        if (string.IsNullOrWhiteSpace(config.PrivateKeyBase64))
        {
            throw new InvalidOperationException("Thiếu cấu hình khóa ký IntegrityHost:PrivateKeyBase64. IntegrityHost yêu cầu khóa riêng ECDSA P-256 cố định, không được fallback tự sinh khóa ngẫu nhiên.");
        }
        _keyId = !string.IsNullOrWhiteSpace(config.SigningKeyId) ? config.SigningKeyId : "dpr-ecdsa-2026-v1";

        _privateKey = EcdsaAttestation.ImportPrivateKeyBase64(config.PrivateKeyBase64);

        _publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(_privateKey);
    }

    public string KeyId => _keyId;

    public string PublicKeyBase64 => _publicKeyBase64;

    public string Sign(string message)
    {
        return EcdsaAttestation.Sign(_privateKey, message);
    }

    public void Dispose()
    {
        _privateKey.Dispose();
    }
}
