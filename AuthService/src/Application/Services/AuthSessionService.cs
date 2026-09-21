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
    private static readonly TimeSpan GoogleLoginTicketLifetime = TimeSpan.FromSeconds(90);

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
    public async Task<string?> CreateGoogleLoginTicketAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string ticket = RefreshTokenUtility.Create();
        bool created = await sessionRepository.CreateGoogleLoginTicketAsync(
            userId,
            RefreshTokenUtility.ComputeHash(ticket),
            utcNow,
            utcNow.Add(GoogleLoginTicketLifetime),
            cancellationToken);

        return created ? ticket : null;
    }

    /// <inheritdoc />
    public async Task<AuthTokenPair?> ExchangeGoogleLoginTicketAsync(string ticket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return null;
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string refreshToken = RefreshTokenUtility.Create();
        AuthSessionPrincipal? principal = await sessionRepository.ConsumeGoogleLoginTicketAndCreateSessionAsync(
            RefreshTokenUtility.ComputeHash(ticket),
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
        IssuedAccessToken accessToken = accessTokenIssuer.Issue(new AccessTokenRequest(
            principal.UserId,
            principal.Roles,
            principal.Permissions,
            principal.OrganizationIds));
        return new AuthTokenPair(accessToken.Value, refreshToken);
    }

}
