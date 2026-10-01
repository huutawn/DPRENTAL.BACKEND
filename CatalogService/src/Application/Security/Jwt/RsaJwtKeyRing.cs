using System.Security.Cryptography;
using Dprental.CatalogService.Application.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.CatalogService.Application.Security.Jwt;

/// <summary>Quản lý các public key được chấp nhận để xác thực token phía CatalogService.</summary>
public sealed class RsaJwtKeyRing : IDisposable
{
    /// <summary>Độ dài RSA tối thiểu được chấp nhận để bảo vệ chữ ký JWT.</summary>
    private const int MinimumRsaKeySizeInBits = 2048;

    /// <summary>Các đối tượng RSA cần được giải phóng cùng vòng đời của container DI.</summary>
    private readonly List<RSA> rsaInstances = [];

    /// <summary>Public key được lập chỉ mục theo <c>kid</c> để xác thực token nhanh và chính xác.</summary>
    private readonly Dictionary<string, SecurityKey> verificationKeys = new(StringComparer.Ordinal);

    public RsaJwtKeyRing(IOptions<JwtOptions> options)
        : this(options.Value)
    {
    }

    public RsaJwtKeyRing(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            throw new ArgumentException("Auth:Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new ArgumentException("Auth:Jwt:Audience is required.");
        }

        if (options.Keys == null || options.Keys.Count == 0)
        {
            throw new ArgumentException("Auth:Jwt:Keys must contain at least one public key.");
        }

        foreach (JwtKeyOptions keyOptions in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(keyOptions.KeyId))
            {
                throw new ArgumentException("Each Auth:Jwt:Keys entry must have a KeyId.");
            }

            if (string.IsNullOrWhiteSpace(keyOptions.PublicKeyPem))
            {
                throw new ArgumentException($"Auth:Jwt:Keys key '{keyOptions.KeyId}' must have a PublicKeyPem.");
            }

            if (verificationKeys.ContainsKey(keyOptions.KeyId))
            {
                throw new ArgumentException($"Auth:Jwt:Keys contains duplicate KeyId '{keyOptions.KeyId}'.");
            }

            string rawPem = ReadPem(keyOptions.PublicKeyPem);
            EnsureNotPrivateKey(keyOptions.KeyId, rawPem);

            RSA publicRsa = RSA.Create();
            rsaInstances.Add(publicRsa);

            try
            {
                publicRsa.ImportFromPem(rawPem);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Auth:Jwt:Keys key '{keyOptions.KeyId}' has invalid PEM content: {ex.Message}", ex);
            }

            try
            {
                RSAParameters checkParameters = publicRsa.ExportParameters(includePrivateParameters: true);
                if (checkParameters.D is not null)
                {
                    throw new ArgumentException($"Auth:Jwt:Keys key '{keyOptions.KeyId}' must not contain a private key.");
                }
            }
            catch (CryptographicException)
            {
                // ExportParameters(true) ném lỗi khi không có private key, đây là trạng thái hợp lệ của public key.
            }

            RSAParameters parameters = publicRsa.ExportParameters(false);
            EnsureSafeRsaKeySize(keyOptions.KeyId, parameters);

            verificationKeys.Add(keyOptions.KeyId, CreateSecurityKey(publicRsa, keyOptions.KeyId));
        }
    }

    /// <summary>Trả về public key tương ứng với <c>kid</c> trong JWT header; thiếu hoặc không nhận diện được trả về rỗng.</summary>
    public IEnumerable<SecurityKey> ResolveVerificationKeys(string? keyId)
    {
        return !string.IsNullOrWhiteSpace(keyId) && verificationKeys.TryGetValue(keyId, out SecurityKey? key)
            ? [key]
            : [];
    }

    public void Dispose()
    {
        foreach (RSA rsa in rsaInstances)
        {
            rsa.Dispose();
        }
    }

    private static void EnsureNotPrivateKey(string keyId, string pem)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Auth:Jwt:Keys key '{keyId}' must not contain a private key.");
        }
    }

    private static void EnsureSafeRsaKeySize(string keyId, RSAParameters parameters)
    {
        int keySizeInBits = (parameters.Modulus?.Length ?? 0) * 8;
        if (keySizeInBits < MinimumRsaKeySizeInBits)
        {
            throw new ArgumentException($"Auth:Jwt:Keys key '{keyId}' must use an RSA key of at least {MinimumRsaKeySizeInBits} bits.");
        }
    }

    private static string ReadPem(string pemOrPath)
    {
        string candidate = pemOrPath.Trim();
        return candidate.StartsWith("-----BEGIN ", StringComparison.Ordinal)
            ? candidate
            : File.ReadAllText(pemOrPath);
    }

    private static RsaSecurityKey CreateSecurityKey(RSA rsa, string keyId)
    {
        return new RsaSecurityKey(rsa)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
    }
}
