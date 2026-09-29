using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Application.DTOs.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

/// <summary>Kiểm tra use case session mà không phụ thuộc Postgres hay private RSA key thật.</summary>
public sealed class AuthSessionServiceTests
{
    [Fact]
    public async Task CreateSessionAsyncPersistsHashAndIssuesTokenPair()
    {
        DateTimeOffset now = new(2026, 9, 17, 7, 30, 0, TimeSpan.Zero);
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), ["member"]));
        AuthSessionService service = CreateService(repository, now);

        Result<AuthTokenPair> result = await service.CreateSessionAsync(repository.Principal!.UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value.AccessToken);
        Assert.NotEqual(result.Value.RefreshToken, repository.CreatedRefreshTokenHash);
        Assert.Equal(64, repository.CreatedRefreshTokenHash!.Length);
        Assert.Equal(now.UtcDateTime, repository.CreatedAtUtc);
        Assert.Equal(now.AddDays(30).UtcDateTime, repository.ExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshAsyncRotatesTokenAndIssuesNewAccessToken()
    {
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), ["admin"]));
        AuthSessionService service = CreateService(repository, DateTimeOffset.UnixEpoch);

        Result<AuthTokenPair> result = await service.RefreshAsync("refresh-token-cu");

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value.AccessToken);
        Assert.NotEqual("refresh-token-cu", result.Value.RefreshToken);
        Assert.Equal(64, repository.ReplacementRefreshTokenHash!.Length);
    }

    [Fact]
    public async Task RefreshAsyncEmptyTokenDoesNotAccessPersistence()
    {
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), []));
        AuthSessionService service = CreateService(repository, DateTimeOffset.UtcNow);

        Result<AuthTokenPair> result = await service.RefreshAsync(" ");

        Assert.True(result.IsFailure);
        Assert.False(repository.RotateCalled);
    }

    [Fact]
    public async Task RevokeByRefreshTokenAsyncEmptyTokenIsIdempotent()
    {
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), []));
        AuthSessionService service = CreateService(repository, DateTimeOffset.UtcNow);

        Result result = await service.RevokeByRefreshTokenAsync(string.Empty);

        Assert.True(result.IsSuccess);
        Assert.False(repository.RevokeCalled);
    }

    [Fact]
    public async Task CreateGoogleLoginTicketAsyncPersistsOnlyHashWithNinetySecondLifetime()
    {
        DateTimeOffset now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), []));
        AuthSessionService service = CreateService(repository, now);

        Result<string> result = await service.CreateGoogleLoginTicketAsync(repository.Principal!.UserId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.NotEqual(result.Value, repository.CreatedTicketHash);
        Assert.Equal(64, repository.CreatedTicketHash!.Length);
        Assert.Equal(now.UtcDateTime, repository.TicketCreatedAtUtc);
        Assert.Equal(now.AddSeconds(90).UtcDateTime, repository.TicketExpiresAtUtc);
    }

    [Fact]
    public async Task ExchangeGoogleLoginTicketAsyncCreatesSessionAndIssuesTokenPair()
    {
        DateTimeOffset now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), ["member"]));
        AuthSessionService service = CreateService(repository, now);

        Result<AuthTokenPair> result = await service.ExchangeGoogleLoginTicketAsync("google-ticket");

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value.AccessToken);
        Assert.NotEqual(result.Value.RefreshToken, repository.ExchangedRefreshTokenHash);
        Assert.Equal(64, repository.ExchangedTicketHash!.Length);
        Assert.Equal(64, repository.ExchangedRefreshTokenHash!.Length);
        Assert.Equal(now.UtcDateTime, repository.ExchangeAtUtc);
        Assert.Equal(now.AddDays(30).UtcDateTime, repository.ExchangeSessionExpiresAtUtc);
    }

    private sealed class RecordingAuthSessionRepository(AuthSessionPrincipal? principal) : IAuthSessionRepository
    {
        public AuthSessionPrincipal? Principal { get; } = principal;
        public string? CreatedRefreshTokenHash { get; private set; }
        public DateTime CreatedAtUtc { get; private set; }
        public DateTime ExpiresAtUtc { get; private set; }
        public string? ReplacementRefreshTokenHash { get; private set; }
        public bool RotateCalled { get; private set; }
        public bool RevokeCalled { get; private set; }
        public string? CreatedTicketHash { get; private set; }
        public DateTime TicketCreatedAtUtc { get; private set; }
        public DateTime TicketExpiresAtUtc { get; private set; }
        public string? ExchangedTicketHash { get; private set; }
        public string? ExchangedRefreshTokenHash { get; private set; }
        public DateTime ExchangeAtUtc { get; private set; }
        public DateTime ExchangeSessionExpiresAtUtc { get; private set; }

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AuthSessionPrincipal?> CreateAsync(
            Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc,
            string? deviceName, string? ipAddress, CancellationToken cancellationToken = default)
        {
            CreatedRefreshTokenHash = refreshTokenHash;
            CreatedAtUtc = utcNow;
            ExpiresAtUtc = expiresAtUtc;
            return Task.FromResult(Principal);
        }

        public Task<RefreshToken?> GetRefreshTokenWithSessionAndUserAsync(string tokenHash, CancellationToken cancellationToken = default)
        {
            if (Principal is null) return Task.FromResult<RefreshToken?>(null);
            User user = new() { Id = Principal.UserId, IsActive = true };
            foreach (string roleName in Principal.Roles)
            {
                user.UserRoles.Add(new UserRole { Role = new Role { Name = roleName } });
            }
            Session session = new() { Id = Guid.NewGuid(), UserId = user.Id, User = user, ExpiresAtUtc = DateTime.UtcNow.AddDays(30) };
            RefreshToken token = new()
            {
                Id = Guid.NewGuid(),
                TokenHash = tokenHash,
                SessionId = session.Id,
                Session = session,
                UserId = user.Id,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(30)
            };
            return Task.FromResult<RefreshToken?>(token);
        }

        // ĐÃ SỬA: Đổi tên thành RotateAsync, rút gọn tham số và trả về Task<AuthSessionPrincipal?>
        public Task<AuthSessionPrincipal?> RotateAsync(
            string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            RotateCalled = true;
            ReplacementRefreshTokenHash = replacementRefreshTokenHash;
            return Task.FromResult(Principal);
        }

        public Task<bool> CreateGoogleLoginTicketAsync(Guid userId, string ticketHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            CreatedTicketHash = ticketHash;
            TicketCreatedAtUtc = utcNow;
            TicketExpiresAtUtc = expiresAtUtc;
            return Task.FromResult(Principal?.UserId == userId);
        }

        public Task<AuthSessionPrincipal?> ConsumeGoogleLoginTicketAndCreateSessionAsync(
            string ticketHash, string refreshTokenHash, DateTime utcNow, DateTime sessionExpiresAtUtc, CancellationToken cancellationToken = default)
        {
            ExchangedTicketHash = ticketHash;
            ExchangedRefreshTokenHash = refreshTokenHash;
            ExchangeAtUtc = utcNow;
            ExchangeSessionExpiresAtUtc = sessionExpiresAtUtc;
            return Task.FromResult(Principal);
        }

        public Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            RevokeCalled = true;
            return Task.CompletedTask;
        }

        public Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid?> GetUserIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult(Principal?.UserId);
        public Task<IReadOnlyList<Session>> GetSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<Guid?> GetSessionIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<bool> RevokeSessionForUserAsync(Guid userId, Guid sessionId, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static AuthSessionService CreateService(RecordingAuthSessionRepository repository, DateTimeOffset now) =>
        new(
            repository,
            new FakeUserRepo(repository.Principal),
            new FakeAccessTokenIssuer(),
            new FixedTimeProvider(now),
            new TestCorrelationIdProvider(),
            new TestAuditLogRepository(),
            NullLogger<AuthSessionService>.Instance,
            new FakeHttpContextAccessor()
        );

    private sealed class FakeUserRepo(AuthSessionPrincipal? principal) : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (principal is null || principal.UserId != id) return Task.FromResult<User?>(null);
            return Task.FromResult<User?>(new User { Id = id, IsActive = true });
        }

        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (principal is null || principal.UserId != id) return Task.FromResult<User?>(null);
            User user = new() { Id = id, IsActive = true };
            foreach (string role in principal.Roles)
            {
                user.UserRoles.Add(new UserRole { Role = new Role { Name = role } });
            }
            return Task.FromResult<User?>(user);
        }

        public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(principal is not null && principal.UserId == userId);

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        // Đã thêm tham số string? type
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, string? search, string? type, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        // Bổ sung hàm GetByIdWithRolesAsync còn thiếu
        public Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
    }

    private sealed class TestCorrelationIdProvider : ICorrelationIdProvider
    {
        public string GetCorrelationId() => "test-correlation-id";
    }

    private sealed class TestAuditLogRepository : IAuditLogRepository
    {
        public Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLog>> GetRecentLogsAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(AccessTokenRequest request) => new("access-token", DateTimeOffset.UnixEpoch.AddMinutes(15));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}