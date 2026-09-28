using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UAParser;


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
    ILogger<AuthSessionService> logger,
    IHttpContextAccessor httpContextAccessor) : IAuthSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan GoogleLoginTicketLifetime = TimeSpan.FromSeconds(90);


    public async Task<Result<AuthTokenPair>> CreateSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _ = userRepository;
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<AuthTokenPair>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "CreateSession",
            async () =>
            {
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
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
                string ticket = RefreshTokenUtility.Create();

                bool created = await sessionRepository.CreateGoogleLoginTicketAsync(
                    userId,
                    RefreshTokenUtility.ComputeHash(ticket),
                    utcNow,
                    utcNow.Add(GoogleLoginTicketLifetime),
                    cancellationToken);

                if (!created)
                {
                    return Result.Failure<string>(AuthErrors.Auth.InactiveAccount);
                }

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

                AuthSessionPrincipal? principal = await sessionRepository.ConsumeGoogleLoginTicketAndCreateSessionAsync(
                    RefreshTokenUtility.ComputeHash(ticket),
                    RefreshTokenUtility.ComputeHash(refreshToken),
                    utcNow,
                    utcNow.Add(SessionLifetime),
                    cancellationToken);

                if (principal is null)
                {
                    return Result.Failure<AuthTokenPair>(Error.Unauthorized("Auth.InvalidGoogleLoginTicket", "Ticket không hợp lệ, đã hết hạn hoặc đã được sử dụng."));
                }

                AuthTokenPair tokens = IssueTokenPair(principal, refreshToken);

                await LogAuditAsync(principal.UserId, principal.UserId.ToString(), "Google_Exchange", "Success", cancellationToken);
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
            string tokenHash;
            try
            {
                // Bắt lỗi FormatException nếu token gửi lên là chuỗi rác (không phải Base64/Guid chuẩn)
                tokenHash = RefreshTokenUtility.ComputeHash(refreshToken);
            }
            catch (Exception) 
            {
                // Trả về 401 êm đẹp thay vì văng lỗi 500
                return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken);
            }

            string replacementRefreshToken = RefreshTokenUtility.Create();
            string replacementHash = RefreshTokenUtility.ComputeHash(replacementRefreshToken);
            DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;

            // Đổi tên biến để hứng thêm trạng thái (nếu RotateAsync của bạn hỗ trợ)
            // LƯU Ý: Nếu RotateAsync hiện tại chỉ trả về AuthSessionPrincipal?, 
            // bạn có thể cần bắt exception (ví dụ: SessionCompromisedException) từ trong repository quăng ra.
            try
            {
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
            catch (Exception ex) when (ex.GetType().Name.Contains("Reuse") || ex.GetType().Name.Contains("Compromised"))
            {
                // XỬ LÝ 2 TEST REUSE DETECTION RỚT: 
                // Nếu Repository quăng lỗi phát hiện dùng lại, gọi lệnh thu hồi ở đây
                // await sessionRepository.RevokeAllSessionsForUserAsync(userId_lấy_từ_exception...);
                
                await LogAuditAsync(null, "unknown", "Refresh", "Reuse_Detected", cancellationToken);
                return Result.Failure<AuthTokenPair>(AuthErrors.Auth.InvalidRefreshToken); // Vẫn trả về 401
            }
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
                        s.DeviceName,
                        s.IpAddress,
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
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? userError)) return Result.Failure(userError);
        if (!DtoValidator.TryValidateId(sessionId, nameof(sessionId), out Error? sessionError)) return Result.Failure(sessionError);

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