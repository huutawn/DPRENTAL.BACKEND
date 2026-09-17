using System.Security.Cryptography;
using Dprental.AuthService.Api.Authentication.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Api.Authentication.Infrastructure;

/// <summary>
/// Holds the active private key for signing and all public keys accepted for verification.
/// </summary>
internal sealed class RsaJwtKeyRing : IDisposable
{
    private const int MinimumRsaKeySizeInBits = 2048;
    private readonly List<RSA> rsaInstances = [];
    private readonly Dictionary<string, SecurityKey> verificationKeys = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<JwkDocument> jwks;

    public RsaJwtKeyRing(IOptions<JwtOptions> options)
        : this(options.Value)
    {
    }

    public RsaJwtKeyRing(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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

            RSA publicRsa = RSA.Create();
            rsaInstances.Add(publicRsa);
            publicRsa.ImportFromPem(keyOptions.PublicKeyPem);

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

        RSA privateRsa = RSA.Create();
        rsaInstances.Add(privateRsa);
        privateRsa.ImportFromPem(activeKeyOptions.PrivateKeyPem);
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

    public RsaSecurityKey ActiveSigningKey { get; }

    public IEnumerable<SecurityKey> ResolveVerificationKeys(string? keyId)
    {
        return !string.IsNullOrWhiteSpace(keyId) && verificationKeys.TryGetValue(keyId, out SecurityKey? key)
            ? [key]
            : [];
    }

    public JwksDocument CreateJwks() => new(jwks);

    public void Dispose()
    {
        foreach (RSA rsa in rsaInstances)
        {
            rsa.Dispose();
        }
    }

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

    private static RsaSecurityKey CreateSecurityKey(RSA rsa, string keyId)
    {
        return new RsaSecurityKey(rsa)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
    }

    private static void EnsureSafeRsaKeySize(string keyId, RSAParameters parameters)
    {
        int keySizeInBits = (parameters.Modulus?.Length ?? 0) * 8;
        if (keySizeInBits < MinimumRsaKeySizeInBits)
        {
            throw new ArgumentException($"Auth:Jwt:Keys key '{keyId}' must use an RSA key of at least {MinimumRsaKeySizeInBits} bits.");
        }
    }

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

internal sealed record JwksDocument(IReadOnlyList<JwkDocument> Keys);

internal sealed record JwkDocument(string Kty, string Kid, string Use, string Alg, string N, string E);
