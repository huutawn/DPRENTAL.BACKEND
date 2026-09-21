using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;

namespace Dprental.AuthService.Application.Services;

/// <summary>Điều phối session persistence, refresh-token rotation và phát hành access token.</summary>
public sealed class AuthSessionService(
    IAuthSessionRepository sessionRepository,
    IAccessTokenIssuer accessTokenIssuer,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IAuditLogRepository auditLogRepository) : IAuthSessionService
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

        if (principal is null)
        {
            return null;
        }

        AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);
        await LogAuditAsync(userId, userId.ToString(), "Login", "Success", cancellationToken);
        return tokens;
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

        if (principal is null)
        {
            await LogAuditAsync(null, "unknown", "Refresh", "Failed_InvalidOrReusedToken", cancellationToken);
            return null;
        }

        AuthTokenPair tokens = IssueTokenPair(principal, replacementRefreshToken);
        await LogAuditAsync(principal.UserId, principal.UserId.ToString(), "Refresh", "Success", cancellationToken);
        return tokens;
    }

    /// <inheritdoc />
    public async Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        await sessionRepository.RevokeByRefreshTokenHashAsync(
            RefreshTokenUtility.ComputeHash(refreshToken),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        await LogAuditAsync(null, "unknown", "Logout", "Success", cancellationToken);
    }

    public async Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await sessionRepository.RevokeAllSessionsForUserAsync(userId, cancellationToken);
        await LogAuditAsync(userId, userId.ToString(), "Session_RevokeAll", "Success", cancellationToken);
    }

    private Task LogAuditAsync(Guid? actorId, string target, string action, string result, CancellationToken cancellationToken) =>
        auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = target,
            Action = action,
            Result = result,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);

    /// <summary>Phát hành access token từ dữ liệu đã được persistence xác thực.</summary>
    private AuthTokenPair IssueTokenPair(AuthSessionPrincipal principal, string refreshToken)
    {
        IssuedAccessToken accessToken = accessTokenIssuer.Issue(new AccessTokenRequest(principal.UserId, principal.Roles));
        return new AuthTokenPair(accessToken.Value, refreshToken);
    }

}
