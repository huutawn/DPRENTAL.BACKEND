using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.CatalogService.Api.Tests;

public static class TestJwtHelper
{
    private static readonly Lazy<(string PublicKeyPem, string PrivateKeyPem, RSA Rsa)> KeyPair = new(() =>
    {
        RSA rsa = RSA.Create(2048);
        return (rsa.ExportRSAPublicKeyPem(), rsa.ExportRSAPrivateKeyPem(), rsa);
    });

    public static string PublicKeyPem => KeyPair.Value.PublicKeyPem;
    public static string PrivateKeyPem => KeyPair.Value.PrivateKeyPem;
    public static RSA RsaInstance => KeyPair.Value.Rsa;

    public const string DefaultKeyId = "key-test-2026";
    public const string DefaultIssuer = "dprental-authservice-dev";
    public const string DefaultAudience = "dprental-clients";

    public static string GenerateToken(
        string? sub = null,
        string? sid = null,
        string? role = "customer",
        string? tokenUse = null,
        string? issuer = DefaultIssuer,
        string? audience = DefaultAudience,
        string? keyId = DefaultKeyId,
        RSA? signingRsa = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        string? algorithm = SecurityAlgorithms.RsaSha256)
    {
        RSA rsa = signingRsa ?? RsaInstance;
        RsaSecurityKey securityKey = new(rsa)
        {
            KeyId = keyId
        };
        SigningCredentials credentials = new(securityKey, algorithm ?? SecurityAlgorithms.RsaSha256);

        List<Claim> claims = [];
        if (sub != null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, sub));
        }

        if (sid != null)
        {
            claims.Add(new Claim("sid", sid));
        }

        if (role != null)
        {
            claims.Add(new Claim("r", role));
        }

        if (tokenUse != null)
        {
            claims.Add(new Claim("u", tokenUse));
        }

        DateTime now = DateTime.UtcNow;
        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore ?? now.AddMinutes(-5),
            expires: expires ?? now.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
