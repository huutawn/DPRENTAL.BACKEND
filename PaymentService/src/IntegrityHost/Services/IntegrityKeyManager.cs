using System.Security.Cryptography;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.IntegrityHost.Options;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.IntegrityHost.Services;

public sealed class IntegrityKeyManager : Dprental.PaymentService.Application.IServices.IIntegritySigningAdapter, IDisposable
{
    private readonly ECDsa _privateKey;
    private readonly string _keyId;
    private readonly string _publicKeyBase64;

    private readonly Dictionary<string, string> _publicKeys = new(StringComparer.Ordinal);
    private readonly string _environment;
    private readonly string _issuer;
    private readonly string _audience;

    public IntegrityKeyManager(IOptions<IntegrityHostOptions> options)
    {
        var config = options?.Value ?? new IntegrityHostOptions();
        string? privateKeyBase64 = null;

        if (!string.IsNullOrWhiteSpace(config.PrivateKeyFilePath))
        {
            if (!File.Exists(config.PrivateKeyFilePath))
            {
                throw new InvalidOperationException($"Không tìm thấy file secret khóa ký tại '{config.PrivateKeyFilePath}'. Startup bị dừng; không được tự ý fallback sang cấu hình khác.");
            }
            try
            {
                privateKeyBase64 = File.ReadAllText(config.PrivateKeyFilePath).Trim();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Lỗi đọc file secret khóa ký tại '{config.PrivateKeyFilePath}': {ex.Message}. Startup bị dừng.", ex);
            }
        }
        else if (!string.IsNullOrWhiteSpace(config.PrivateKeyBase64))
        {
            privateKeyBase64 = config.PrivateKeyBase64.Trim();
        }

        if (string.IsNullOrWhiteSpace(privateKeyBase64))
        {
            throw new InvalidOperationException("Thiếu cấu hình khóa ký IntegrityHost:PrivateKeyBase64 hoặc PrivateKeyFilePath. IntegrityHost yêu cầu khóa riêng ECDSA P-256 cố định, không được fallback tự sinh khóa ngẫu nhiên.");
        }

        _keyId = !string.IsNullOrWhiteSpace(config.SigningKeyId) ? config.SigningKeyId.Trim() : "dpr-ecdsa-2026-v1";
        _environment = !string.IsNullOrWhiteSpace(config.Environment) ? config.Environment.Trim() : "production";
        _issuer = !string.IsNullOrWhiteSpace(config.Issuer) ? config.Issuer.Trim() : "dprental-integrity-host";
        _audience = !string.IsNullOrWhiteSpace(config.Audience) ? config.Audience.Trim() : "payment-api";

        _privateKey = EcdsaAttestation.ImportPrivateKeyBase64(privateKeyBase64);
        _publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(_privateKey);
        _publicKeys[_keyId] = _publicKeyBase64;

        if (config.OldPublicKeys is not null)
        {
            foreach (var (kId, pubKey) in config.OldPublicKeys)
            {
                if (!string.IsNullOrWhiteSpace(kId) && !string.IsNullOrWhiteSpace(pubKey))
                {
                    if (!string.Equals(kId, _keyId, StringComparison.Ordinal))
                    {
                        _publicKeys[kId.Trim()] = pubKey.Trim();
                    }
                }
            }
        }
    }

    public string KeyId => _keyId;
    public string PublicKeyBase64 => _publicKeyBase64;
    public string Environment => _environment;
    public string Issuer => _issuer;
    public string Audience => _audience;

    public string? GetPublicKey(string keyId)
    {
        return _publicKeys.TryGetValue(keyId, out var pubKey) ? pubKey : null;
    }

    public string Sign(string message)
    {
        return EcdsaAttestation.Sign(_privateKey, message);
    }

    public void Dispose()
    {
        _privateKey.Dispose();
    }
}
