using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Application.DTOs.Auth;
using UAParser;
using Microsoft.AspNetCore.Http;
using Dprental.AuthService.Domain.ValueObjects;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Điều phối session persistence, refresh-token rotation và phát hành access token.
/// Kế thừa kiến trúc Result<T> và tích hợp nhận diện IP/DeviceName.
/// </summary>
public sealed class AuthSessionService(
    IAuthSessionRepository sessionRepository,
    IUserRepository userRepository,
    IAccessTokenIssuer accessTokenIssuer,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IAuditLogRepository auditLogRepository,
    IHttpContextAccessor httpContextAccessor) : IAuthSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    public async Task<Result<AuthTokenPair>> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _ = userRepository;
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<AuthTokenPair>(validationError);
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string refreshToken = RefreshTokenUtility.Create();

        // --- LẤY THÔNG TIN THIẾT BỊ VÀ IP ---
        string? userAgent = httpContextAccessor.HttpContext?.Request.Headers["User-Agent"];
        string? ipAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
        string deviceName = "Thiết bị không xác định";

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var uaParser = Parser.GetDefault();
            var clientInfo = uaParser.Parse(userAgent);
            deviceName = $"{clientInfo.UA.Family} trên {clientInfo.OS.Family}";
        }

        AuthSessionPrincipal? principal = await sessionRepository.CreateAsync(
            userId,
            RefreshTokenUtility.ComputeHash(refreshToken),
            utcNow,
            utcNow.Add(SessionLifetime),
            deviceName,
            ipAddress,
            cancellationToken);

        if (principal is null)
        {
            return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InactiveAccount);
        }

        AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);

        await LogAuditAsync(userId, userId.ToString(), "Login", "Success", cancellationToken);
        return Result.Success(tokens);
    }

    public async Task<Result<AuthTokenPair>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
        }

        string tokenHash;
        try
        {
            tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);
        }
        catch (ArgumentException)
        {
            return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
        }

        string replacementRefreshToken = RefreshTokenUtility.Create();
        string replacementHash = RefreshTokenUtility.ComputeHash(replacementRefreshToken);
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;

        AuthSessionPrincipal? principal = await sessionRepository.RotateAsync(
            tokenHash,
            replacementHash,
            utcNow,
            cancellationToken);

        if (principal is null)
        {
            await LogAuditAsync(null, "unknown", "Refresh", "Failed_Or_Compromised", cancellationToken);
            return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
        }

        AuthTokenPair tokens = IssueTokenPair(principal, replacementRefreshToken);

        await LogAuditAsync(principal.UserId, principal.UserId.ToString(), "Refresh", "Success", cancellationToken);
        return Result.Success(tokens);
    }

    public async Task<Result> RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Success();
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        string tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);

        Guid? userId = await sessionRepository.GetUserIdByTokenHashAsync(tokenHash, cancellationToken);
        await sessionRepository.RevokeByRefreshTokenHashAsync(tokenHash, utcNow, cancellationToken);

        await LogAuditAsync(userId, userId?.ToString() ?? "unknown", "Logout", "Success", cancellationToken);
        return Result.Success();
    }

    public async Task<Result> RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        await sessionRepository.RevokeAllSessionsForUserAsync(userId, cancellationToken);
        await LogAuditAsync(userId, userId.ToString(), "Session_RevokeAll", "Success", cancellationToken);
        return Result.Success();
    }

    public async Task<Result<Guid>> GetCurrentSessionIdAsync(string? currentRefreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            return Result.Failure<Guid>(AuthErrors.Auth.InvalidRefreshToken);
        }

        string tokenHash = RefreshTokenUtility.ComputeHash(currentRefreshToken);
        Guid? sessionId = await sessionRepository.GetSessionIdByTokenHashAsync(tokenHash, cancellationToken);
        if (sessionId is null)
        {
            return Result.Failure<Guid>(AuthErrors.Auth.SessionNotFound);
        }

        return Result.Success(sessionId.Value);
    }

    public async Task<Result<IReadOnlyList<SessionResponse>>> GetSessionsAsync(
        Guid userId,
        string? currentRefreshToken,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<IReadOnlyList<SessionResponse>>(validationError);
        }

        Guid? currentSessionId = null;
        if (!string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            currentSessionId = await sessionRepository.GetSessionIdByTokenHashAsync(
                RefreshTokenUtility.ComputeHash(currentRefreshToken),
                cancellationToken);
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        IReadOnlyList<Session> sessions = await sessionRepository.GetSessionsForUserAsync(userId, cancellationToken);

        IReadOnlyList<SessionResponse> response = sessions
            .Select(s => new SessionResponse(
                s.Id,
                s.DeviceName,
                s.IpAddress,
                s.CreatedAtUtc,
                s.ExpiresAtUtc,
                s.RevokedAtUtc,
                s.RevokedAtUtc is null && s.ExpiresAtUtc > utcNow,
                currentSessionId.HasValue && s.Id == currentSessionId.Value))
            .ToList();

        return Result.Success(response);
    }

    public async Task<Result> RevokeSessionAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? userError)) return Result.Failure(userError);
        if (!DtoValidator.TryValidateId(sessionId, nameof(sessionId), out Error? sessionError)) return Result.Failure(sessionError);

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
