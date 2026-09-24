using System.Security.Cryptography;
using Dprental.AuthService.Application.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Application.Security.Jwt;

/// <summary>Quản lý private key active để ký và các public key được chấp nhận để xác thực.</summary>
public sealed class RsaJwtKeyRing : IDisposable
{
    /// <summary>Độ dài RSA tối thiểu được chấp nhận để bảo vệ chữ ký JWT.</summary>
    private const int MinimumRsaKeySizeInBits = 2048;

    /// <summary>Các đối tượng RSA cần được giải phóng cùng vòng đời của container DI.</summary>
    private readonly List<RSA> rsaInstances = [];

    /// <summary>Public key được lập chỉ mục theo <c>kid</c> để xác thực token nhanh và chính xác.</summary>
    private readonly Dictionary<string, SecurityKey> verificationKeys = new(StringComparer.Ordinal);

    /// <summary>Bản JWKS chỉ chứa public key, được tái sử dụng cho các HTTP response.</summary>
    private readonly IReadOnlyList<JwkDocument> jwks;

    /// <summary>Khởi tạo key ring từ options đã được bind bởi dependency injection.</summary>
    public RsaJwtKeyRing(IOptions<JwtOptions> options)
        : this(options.Value)
    {
    }

    /// <summary>Khởi tạo key ring từ cấu hình cụ thể; dùng cả khi validate lúc startup.</summary>
    public RsaJwtKeyRing(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Giữ RSA parameters public để kiểm tra active private key thực sự thuộc cùng key pair.
        Dictionary<string, RSAParameters> publicKeyParameters = new(StringComparer.Ordinal);
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

            // Chỉ public key được đưa vào danh sách xác thực và JWKS.
            RSA publicRsa = RSA.Create();
            rsaInstances.Add(publicRsa);
            publicRsa.ImportFromPem(ReadPem(keyOptions.PublicKeyPem));

            RSAParameters parameters = publicRsa.ExportParameters(false);
            EnsureSafeRsaKeySize(keyOptions.KeyId, parameters);

            verificationKeys.Add(keyOptions.KeyId, CreateSecurityKey(publicRsa, keyOptions.KeyId));
            publicKeyParameters.Add(keyOptions.KeyId, parameters);
        }

        JwtKeyOptions? activeKeyOptions = options.Keys.SingleOrDefault(key => string.Equals(key.KeyId, options.ActiveKeyId, StringComparison.Ordinal));
        if (activeKeyOptions is null)
        {
            throw new ArgumentException($"Auth:Jwt:ActiveKeyId '{options.ActiveKeyId}' does not exist in Auth:Jwt:Keys.");
        }

        if (string.IsNullOrWhiteSpace(activeKeyOptions.PrivateKeyPem))
        {
            throw new ArgumentException($"Active signing key '{options.ActiveKeyId}' must have a PrivateKeyPem.");
        }

        // Private key chỉ được nạp cho key active và chỉ được dùng để ký token mới.
        RSA privateRsa = RSA.Create();
        rsaInstances.Add(privateRsa);
        privateRsa.ImportFromPem(ReadPem(activeKeyOptions.PrivateKeyPem));
        RSAParameters privateKeyParameters = privateRsa.ExportParameters(true);
        if (privateKeyParameters.D is null)
        {
            throw new ArgumentException($"Active signing key '{options.ActiveKeyId}' must contain an RSA private key.");
        }

        EnsureSafeRsaKeySize(activeKeyOptions.KeyId, privateKeyParameters);
        if (!HasSamePublicKey(privateKeyParameters, publicKeyParameters[activeKeyOptions.KeyId]))
        {
            throw new ArgumentException($"Active signing key '{options.ActiveKeyId}' does not match its PublicKeyPem.");
        }

        ActiveSigningKey = CreateSecurityKey(privateRsa, activeKeyOptions.KeyId);
        jwks = options.Keys
            .Select(key => CreateJwk(key.KeyId, publicKeyParameters[key.KeyId]))
            .ToArray();
    }

    /// <summary>Private RSA key của entry active, chỉ dùng để tạo chữ ký RS256.</summary>
    public RsaSecurityKey ActiveSigningKey { get; }

    /// <summary>Trả về public key tương ứng với <c>kid</c> trong JWT header; không fallback key khác.</summary>
    public IEnumerable<SecurityKey> ResolveVerificationKeys(string? keyId)
    {
        return !string.IsNullOrWhiteSpace(keyId) && verificationKeys.TryGetValue(keyId, out SecurityKey? key)
            ? [key]
            : [];
    }

    /// <summary>Tạo payload JWKS công khai từ các public verification key hiện có.</summary>
    public JwksDocument CreateJwks() => new(jwks);

    /// <summary>Giải phóng toàn bộ handle RSA đã được nạp từ PEM.</summary>
    public void Dispose()
    {
        foreach (RSA rsa in rsaInstances)
        {
            rsa.Dispose();
        }
    }

    /// <summary>Chuyển public RSA parameters sang một JWK chuẩn, không bao gồm private exponent.</summary>
    private static JwkDocument CreateJwk(string keyId, RSAParameters parameters)
    {
        return new JwkDocument(
            "RSA",
            keyId,
            "sig",
            SecurityAlgorithms.RsaSha256,
            Base64UrlEncoder.Encode(parameters.Modulus),
            Base64UrlEncoder.Encode(parameters.Exponent));
    }

    /// <summary>Tạo SecurityKey có <c>kid</c> và không cache provider để không giữ RSA đã dispose.</summary>
    private static RsaSecurityKey CreateSecurityKey(RSA rsa, string keyId)
    {
        return new RsaSecurityKey(rsa)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
    }

    private static string ReadPem(string pemOrPath)
    {
        string candidate = pemOrPath.Trim();
        return candidate.StartsWith("-----BEGIN ", StringComparison.Ordinal)
            ? candidate
            : File.ReadAllText(pemOrPath);
    }

    /// <summary>Từ chối RSA key yếu trước khi key được dùng để ký hoặc xác thực token.</summary>
    private static void EnsureSafeRsaKeySize(string keyId, RSAParameters parameters)
    {
        int keySizeInBits = (parameters.Modulus?.Length ?? 0) * 8;
        if (keySizeInBits < MinimumRsaKeySizeInBits)
        {
            throw new ArgumentException($"Auth:Jwt:Keys key '{keyId}' must use an RSA key of at least {MinimumRsaKeySizeInBits} bits.");
        }
    }

    /// <summary>So sánh modulus và exponent để xác nhận private/public PEM thuộc cùng một key pair.</summary>
    private static bool HasSamePublicKey(RSAParameters first, RSAParameters second)
    {
        return first.Modulus is not null
            && first.Exponent is not null
            && second.Modulus is not null
            && second.Exponent is not null
            && first.Modulus.AsSpan().SequenceEqual(second.Modulus)
            && first.Exponent.AsSpan().SequenceEqual(second.Exponent);
    }
}

/// <summary>Payload JWKS được endpoint công khai trả về theo RFC 7517.</summary>
public sealed record JwksDocument(
    /// <summary>Danh sách public JWK có thể xác thực access token còn hiệu lực.</summary>
    IReadOnlyList<JwkDocument> Keys);

/// <summary>Một JSON Web Key RSA công khai, không bao giờ chứa thành phần private.</summary>
public sealed record JwkDocument(
    /// <summary>Loại khóa chuẩn JWK, luôn là <c>RSA</c>.</summary>
    string Kty,
    /// <summary>Định danh khóa khớp với trường <c>kid</c> của JWT.</summary>
    string Kid,
    /// <summary>Mục đích dùng khóa, luôn là ký/xác thực chữ ký (<c>sig</c>).</summary>
    string Use,
    /// <summary>Thuật toán chữ ký chuẩn JWK, luôn là <c>RS256</c>.</summary>
    string Alg,
    /// <summary>RSA modulus được Base64Url encode.</summary>
    string N,
    /// <summary>RSA public exponent được Base64Url encode.</summary>
    string E);
