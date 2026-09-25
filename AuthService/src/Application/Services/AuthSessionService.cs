using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Điều phối session persistence, refresh-token rotation và phát hành access token.
/// Service quyết định phát hiện refresh token reuse, thu hồi toàn bộ session và kiểm tra quyền.
/// </summary>
public sealed class AuthSessionService(
    IAuthSessionRepository sessionRepository,
    IUserRepository userRepository,
    IAccessTokenIssuer accessTokenIssuer,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IAuditLogRepository auditLogRepository,
    ILogger<AuthSessionService> logger) : IAuthSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan GoogleLoginTicketLifetime = TimeSpan.FromSeconds(90);

    public async Task<Result<AuthTokenPair>> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<AuthTokenPair>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "CreateSession",
            async () =>
            {
                User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
                if (user is null || !user.IsActive)
                {
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InactiveAccount);
                }

                string refreshToken = RefreshTokenUtility.Create();
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
                DateTime expiresAtUtc = utcNow.Add(SessionLifetime);

                await sessionRepository.CreateSessionAsync(
                    userId,
                    RefreshTokenUtility.ComputeHash(refreshToken),
                    utcNow,
                    expiresAtUtc,
                    cancellationToken);

                AuthSessionPrincipal principal = BuildPrincipal(user);
                AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);

                await LogAuditAsync(userId, userId.ToString(), "Login", "Success", cancellationToken);
                return Result.Success(tokens);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<string>> CreateGoogleLoginTicketAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<string>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "CreateGoogleLoginTicket",
            async () =>
            {
                bool isActive = await userRepository.IsUserActiveAsync(userId, cancellationToken);
                if (!isActive)
                {
                    return Result.Failure<string>(AuthErrors.Auth.InactiveAccount);
                }

                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
                string ticket = RefreshTokenUtility.Create();

                await sessionRepository.CreateGoogleLoginTicketAsync(
                    userId,
                    RefreshTokenUtility.ComputeHash(ticket),
                    utcNow,
                    utcNow.Add(GoogleLoginTicketLifetime),
                    cancellationToken);

                return Result.Success(ticket);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<AuthTokenPair>> ExchangeGoogleLoginTicketAsync(string ticket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return Result.Failure<AuthTokenPair>(Error.Unauthorized("Auth.InvalidGoogleLoginTicket", "Ticket không hợp lệ, đã hết hạn hoặc đã được sử dụng."));
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ExchangeGoogleLoginTicket",
            async () =>
            {
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
                string refreshToken = RefreshTokenUtility.Create();

                Guid? userId = await sessionRepository.ConsumeGoogleLoginTicketAndCreateSessionAsync(
                    RefreshTokenUtility.ComputeHash(ticket),
                    RefreshTokenUtility.ComputeHash(refreshToken),
                    utcNow,
                    utcNow.Add(SessionLifetime),
                    cancellationToken);

                if (userId is null)
                {
                    return Result.Failure<AuthTokenPair>(Error.Unauthorized("Auth.InvalidGoogleLoginTicket", "Ticket không hợp lệ, đã hết hạn hoặc đã được sử dụng."));
                }

                User? user = await userRepository.GetByIdWithAccessAsync(userId.Value, cancellationToken);
                if (user is null || !user.IsActive)
                {
                    await sessionRepository.RevokeAllSessionsForUserAsync(userId.Value, cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InactiveAccount);
                }

                AuthSessionPrincipal principal = BuildPrincipal(user);
                AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);

                await LogAuditAsync(userId.Value, userId.Value.ToString(), "Google_Exchange", "Success", cancellationToken);
                return Result.Success(tokens);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<AuthTokenPair>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RefreshToken",
            async () =>
            {
                string tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;

                RefreshToken? tokenRecord = await sessionRepository.GetRefreshTokenWithSessionAndUserAsync(tokenHash, cancellationToken);
                if (tokenRecord is null)
                {
                    await LogAuditAsync(null, "unknown", "Refresh", "Failed_InvalidToken", cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
                }

                // Phát hiện Refresh Token dùng lại (Reuse Detection)
                // Service quyết định: thu hồi TOÀN BỘ phiên của user trước khi trả lỗi
                if (tokenRecord.IsRevoked || tokenRecord.UsedAtUtc is not null || tokenRecord.RevokedAtUtc is not null)
                {
                    await sessionRepository.RevokeAllSessionsForUserAsync(tokenRecord.UserId, cancellationToken);
                    await LogAuditAsync(tokenRecord.UserId, tokenRecord.UserId.ToString(), "Session_Compromised", "RevokedAll_ReuseDetected", cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
                }

                if (tokenRecord.ExpiresAtUtc <= utcNow || tokenRecord.Session.ExpiresAtUtc <= utcNow || tokenRecord.Session.RevokedAtUtc is not null)
                {
                    await LogAuditAsync(tokenRecord.UserId, tokenRecord.UserId.ToString(), "Refresh", "Failed_Expired", cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
                }

                if (!tokenRecord.Session.User.IsActive)
                {
                    await LogAuditAsync(tokenRecord.UserId, tokenRecord.UserId.ToString(), "Refresh", "Failed_AccountDisabled", cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InactiveAccount);
                }

                string replacementRefreshToken = RefreshTokenUtility.Create();
                string replacementHash = RefreshTokenUtility.ComputeHash(replacementRefreshToken);

                bool rotated = await sessionRepository.RotateTokenAsync(
                    tokenRecord.Id,
                    tokenRecord.SessionId,
                    tokenRecord.UserId,
                    replacementHash,
                    tokenRecord.Session.ExpiresAtUtc,
                    utcNow,
                    cancellationToken);

                if (!rotated)
                {
                    // Race condition: token bị dùng song song ở request khác -> phát hiện reuse
                    await sessionRepository.RevokeAllSessionsForUserAsync(tokenRecord.UserId, cancellationToken);
                    await LogAuditAsync(tokenRecord.UserId, tokenRecord.UserId.ToString(), "Session_Compromised", "RevokedAll_ReuseDetected", cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
                }

                User? user = await userRepository.GetByIdWithAccessAsync(tokenRecord.UserId, cancellationToken);
                if (user is null || !user.IsActive)
                {
                    await sessionRepository.RevokeAllSessionsForUserAsync(tokenRecord.UserId, cancellationToken);
                    return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InactiveAccount);
                }

                AuthSessionPrincipal principal = BuildPrincipal(user);
                AuthTokenPair tokens = IssueTokenPair(principal, replacementRefreshToken);

                await LogAuditAsync(principal.UserId, principal.UserId.ToString(), "Refresh", "Success", cancellationToken);
                return Result.Success(tokens);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> RevokeByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Success();
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RevokeByRefreshToken",
            async () =>
            {
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
                string tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);

                Guid? userId = await sessionRepository.GetUserIdByTokenHashAsync(tokenHash, cancellationToken);
                await sessionRepository.RevokeByRefreshTokenHashAsync(tokenHash, utcNow, cancellationToken);

                await LogAuditAsync(userId, userId?.ToString() ?? "unknown", "Logout", "Success", cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RevokeAllSessions",
            async () =>
            {
                await sessionRepository.RevokeAllSessionsForUserAsync(userId, cancellationToken);
                await LogAuditAsync(userId, userId.ToString(), "Session_RevokeAll", "Success", cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<Guid>> GetCurrentSessionIdAsync(string? currentRefreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            return Result.Failure<Guid>(AuthErrors.Auth.InvalidRefreshToken);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetCurrentSessionId",
            async () =>
            {
                string tokenHash = RefreshTokenUtility.ComputeHash(currentRefreshToken);
                Guid? sessionId = await sessionRepository.GetSessionIdByTokenHashAsync(tokenHash, cancellationToken);
                if (sessionId is null)
                {
                    return Result.Failure<Guid>(AuthErrors.Auth.SessionNotFound);
                }

                return Result.Success(sessionId.Value);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
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

        return await ApplicationExecutor.ExecuteAsync(
            "GetSessions",
            async () =>
            {
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
                        s.CreatedAtUtc,
                        s.ExpiresAtUtc,
                        s.RevokedAtUtc,
                        s.RevokedAtUtc is null && s.ExpiresAtUtc > utcNow,
                        currentSessionId.HasValue && s.Id == currentSessionId.Value))
                    .ToList();

                return Result.Success(response);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> RevokeSessionAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? userError))
        {
            return Result.Failure(userError);
        }

        if (!DtoValidator.TryValidateId(sessionId, nameof(sessionId), out Error? sessionError))
        {
            return Result.Failure(sessionError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RevokeSession",
            async () =>
            {
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
                bool revoked = await sessionRepository.RevokeSessionForUserAsync(userId, sessionId, utcNow, cancellationToken);
                if (!revoked)
                {
                    return Result.Failure(AuthErrors.Auth.SessionNotFound);
                }

                await LogAuditAsync(userId, sessionId.ToString(), "Session_Revoke", "Success", cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    private static AuthSessionPrincipal BuildPrincipal(User user) =>
        new(
            user.Id,
            UserAccessResolver.Roles(user),
            UserAccessResolver.Permissions(user),
            UserAccessResolver.ActiveMemberships(user)
                .Select(membership => membership.OrganizationId)
                .ToArray());

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
<<<<<<< HEAD
            principal.OrganizationIds,
            IsProfileCompleted: principal.IsProfileCompleted));
=======
            principal.OrganizationIds));

>>>>>>> b8a22d3c2f59a49031461f3f63c44a44371afe2d
        return new AuthTokenPair(accessToken.Value, refreshToken);
    }
}
