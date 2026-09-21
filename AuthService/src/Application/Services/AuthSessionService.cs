using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
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
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

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

        if (principal is null) return null;

        AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);
        // Lưu ý: Đăng nhập thành công đã được log bên UserService.AuthenticateAsync, 
        // có thể bỏ log ở đây để tránh trùng lặp 2 dòng log cho 1 lần login.
        return tokens;
    }

    public async Task<AuthTokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        string replacementRefreshToken = RefreshTokenUtility.Create();

        try
        {
            AuthSessionPrincipal? principal = await sessionRepository.RotateAsync(
                RefreshTokenUtility.ComputeHash(refreshToken),
                RefreshTokenUtility.ComputeHash(replacementRefreshToken),
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);

            if (principal is null)
            {
                await LogAuditAsync(null, "unknown", "Refresh", "Failed_InvalidToken", cancellationToken);
                return null;
            }

            AuthTokenPair tokens = IssueTokenPair(principal, replacementRefreshToken);
            await LogAuditAsync(principal.UserId, principal.UserId.ToString(), "Refresh", "Success", cancellationToken);
            return tokens;
        }
        catch (SessionCompromisedException ex)
        {
            // BẮT BỘC PHẢI CÓ CHO MVP: Xử lý Refresh-token reuse (Session Compromise)
            // Kẻ gian đang cố dùng lại token cũ -> Kích hoạt Kill Switch thu hồi toàn bộ session của user này
            await sessionRepository.RevokeAllSessionsForUserAsync(ex.UserId, cancellationToken);
            await LogAuditAsync(ex.UserId, ex.UserId.ToString(), "Session_Compromised", "RevokedAll_ReuseDetected", cancellationToken);
            return null;
        }
    }

    public async Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;

        string tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);

        // MVP: Để có ActorId cho Audit Log, bạn cần lấy UserId từ session trước khi revoke
        Guid? userId = await sessionRepository.GetUserIdByTokenHashAsync(tokenHash, cancellationToken);

        if (userId.HasValue)
        {
            await sessionRepository.RevokeByRefreshTokenHashAsync(tokenHash, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            await LogAuditAsync(userId.Value, userId.Value.ToString(), "Logout", "Success", cancellationToken);
        }
        else
        {
            await LogAuditAsync(null, "unknown", "Logout", "Failed_TokenNotFound", cancellationToken);
        }
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