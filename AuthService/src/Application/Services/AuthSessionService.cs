using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;

namespace Dprental.AuthService.Application.Services;

/// <summary>Điều phối session persistence, refresh-token rotation và phát hành access token.</summary>
public sealed class AuthSessionService(
    IAuthSessionRepository sessionRepository,
    IAccessTokenIssuer accessTokenIssuer,
    TimeProvider timeProvider) : IAuthSessionService
{
    /// <summary>Thời hạn tuyệt đối của một session và mọi refresh token thuộc session đó.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public async Task<AuthTokenPair?> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string refreshToken = RefreshTokenUtility.Create();
        AuthSessionPrincipal? principal = await sessionRepository.CreateAsync(
            userId,
            RefreshTokenUtility.ComputeHash(refreshToken),
            utcNow,
            utcNow.Add(SessionLifetime),
            cancellationToken);

        return principal is null ? null : IssueTokenPair(principal, refreshToken);
    }

    /// <inheritdoc />
    public async Task<AuthTokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        string replacementRefreshToken = RefreshTokenUtility.Create();
        AuthSessionPrincipal? principal = await sessionRepository.RotateAsync(
            RefreshTokenUtility.ComputeHash(refreshToken),
            RefreshTokenUtility.ComputeHash(replacementRefreshToken),
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

        return sessionRepository.RevokeByRefreshTokenHashAsync(
            RefreshTokenUtility.ComputeHash(refreshToken),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
    }

    /// <summary>Phát hành access token từ dữ liệu đã được persistence xác thực.</summary>
    private AuthTokenPair IssueTokenPair(AuthSessionPrincipal principal, string refreshToken)
    {
        IssuedAccessToken accessToken = accessTokenIssuer.Issue(new AccessTokenRequest(principal.UserId, principal.Roles));
        return new AuthTokenPair(accessToken.Value, refreshToken);
    }

}
