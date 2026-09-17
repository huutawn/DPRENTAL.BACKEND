using System.Security.Cryptography;
using System.Text;
using Dprental.AuthService.Application.Authentication.Tokens;

namespace AuthService.Application.Authentication.Sessions;

/// <summary>Điều phối session persistence, refresh-token rotation và phát hành access token.</summary>
public sealed class AuthenticationSessionService(
    IAuthenticationSessionStore sessionStore,
    IAccessTokenIssuer accessTokenIssuer,
    TimeProvider timeProvider) : IAuthenticationSessionService
{
    /// <summary>Thời hạn tuyệt đối của một session và mọi refresh token thuộc session đó.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public async Task<AuthenticationTokenPair?> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string refreshToken = CreateRefreshToken();
        SessionPrincipal? principal = await sessionStore.CreateAsync(
            userId,
            ComputeTokenHash(refreshToken),
            utcNow,
            utcNow.Add(SessionLifetime),
            cancellationToken);

        return principal is null ? null : IssueTokenPair(principal, refreshToken);
    }

    /// <inheritdoc />
    public async Task<AuthenticationTokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        string replacementRefreshToken = CreateRefreshToken();
        SessionPrincipal? principal = await sessionStore.RotateAsync(
            ComputeTokenHash(refreshToken),
            ComputeTokenHash(replacementRefreshToken),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        return principal is null ? null : IssueTokenPair(principal, replacementRefreshToken);
    }

    /// <inheritdoc />
    public Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Task.CompletedTask;
        }

        return sessionStore.RevokeByRefreshTokenHashAsync(
            ComputeTokenHash(refreshToken),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
    }

    /// <summary>Phát hành access token từ dữ liệu đã được persistence xác thực.</summary>
    private AuthenticationTokenPair IssueTokenPair(SessionPrincipal principal, string refreshToken)
    {
        IssuedAccessToken accessToken = accessTokenIssuer.Issue(new AccessTokenRequest(principal.UserId, principal.Roles));
        return new AuthenticationTokenPair(accessToken.Value, refreshToken);
    }

    /// <summary>Sinh token URL-safe từ 64 byte ngẫu nhiên mật mã.</summary>
    private static string CreateRefreshToken()
    {
        Span<byte> randomBytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(randomBytes);
        return Convert.ToBase64String(randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Hash token để dữ liệu bị lộ không cho phép dùng lại refresh token.</summary>
    private static string ComputeTokenHash(string refreshToken) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
