using System.Security.Cryptography;
using System.Text;

namespace Dprental.AuthService.Common.Utils;

/// <summary>Creates opaque refresh tokens and their persistent hashes.</summary>
public static class RefreshTokenUtility
{
    /// <summary>Creates a URL-safe cryptographically-random token.</summary>
    public static string Create()
    {
        Span<byte> randomBytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(randomBytes);
        return Convert.ToBase64String(randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Creates the SHA-256 hash persisted for a refresh token.</summary>
    public static string ComputeHash(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }
}
