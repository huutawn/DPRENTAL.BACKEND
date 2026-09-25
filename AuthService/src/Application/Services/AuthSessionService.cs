using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Http;
using UAParser;

namespace Dprental.AuthService.Application.Services;

/// <summary>Điều phối session persistence, refresh-token rotation và phát hành access token.</summary>
public sealed class AuthSessionService(
    IAuthSessionRepository sessionRepository,
    IAccessTokenIssuer accessTokenIssuer,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IAuditLogRepository auditLogRepository,
    IHttpContextAccessor httpContextAccessor) : IAuthSessionService // <--- Inject IHttpContextAccessor
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan GoogleLoginTicketLifetime = TimeSpan.FromSeconds(90);

    public async Task<AuthTokenPair?> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string refreshToken = RefreshTokenUtility.Create();

        // --- BẮT VÀ DỊCH THÔNG TIN THIẾT BỊ ---
        string? userAgent = httpContextAccessor.HttpContext?.Request.Headers["User-Agent"];
        string? ipAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
        string deviceName = "Thiết bị không xác định";

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var uaParser = Parser.GetDefault();
            var clientInfo = uaParser.Parse(userAgent);
            deviceName = $"{clientInfo.UA.Family} trên {clientInfo.OS.Family}";
        }
        // -------------------------------------

        AuthSessionPrincipal? principal = await sessionRepository.CreateAsync(
            userId,
            RefreshTokenUtility.ComputeHash(refreshToken),
            utcNow,
            utcNow.Add(SessionLifetime),
            deviceName, // <--- Truyền DeviceName xuống Repository
            ipAddress,  // <--- Truyền IpAddress xuống Repository
            cancellationToken);

        if (principal is null) return null;

        AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);
        return tokens;
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
            await sessionRepository.RevokeAllSessionsForUserAsync(ex.UserId, cancellationToken);
            await LogAuditAsync(ex.UserId, ex.UserId.ToString(), "Session_Compromised", "RevokedAll_ReuseDetected", cancellationToken);
            return null;
        }
    }

    public async Task RevokeDeviceSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        await RevokeSessionAsync(userId, sessionId, cancellationToken);
    }

    public async Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;

        string tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);
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

    public async Task<Guid?> GetCurrentSessionIdAsync(string? currentRefreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            return null;
        }

        string tokenHash = RefreshTokenUtility.ComputeHash(currentRefreshToken);
        return await sessionRepository.GetSessionIdByTokenHashAsync(tokenHash, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionResponse>> GetSessionsAsync(
        Guid userId,
        string? currentRefreshToken,
        CancellationToken cancellationToken = default)
    {
        Guid? currentSessionId = await GetCurrentSessionIdAsync(currentRefreshToken, cancellationToken);

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<Session> sessions = await sessionRepository.GetSessionsForUserAsync(userId, cancellationToken);

        // Đã map thêm DeviceName và IpAddress vào Response
        return sessions.Select(s => new SessionResponse(
            s.Id,
            s.DeviceName, 
            s.IpAddress,
            s.CreatedAtUtc,
            s.ExpiresAtUtc,
            s.RevokedAtUtc,
            s.RevokedAtUtc is null && s.ExpiresAtUtc > utcNow,
            currentSessionId.HasValue && s.Id == currentSessionId.Value))
            .ToArray();
    }

    public async Task<Result> RevokeSessionAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        bool revoked = await sessionRepository.RevokeSessionForUserAsync(userId, sessionId, utcNow, cancellationToken);
        if (!revoked)
        {
            return Result.Failure(AuthErrors.Auth.SessionNotFound);
        }

        await LogAuditAsync(userId, sessionId.ToString(), "Session_Revoke", "Success", cancellationToken);
        return Result.Success();
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