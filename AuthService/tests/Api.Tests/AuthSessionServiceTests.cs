using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Entities;
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

        AuthTokenPair? result = await service.CreateSessionAsync(repository.Principal!.UserId);

        Assert.NotNull(result);
        Assert.Equal("access-token", result.AccessToken);
        Assert.NotEqual(result.RefreshToken, repository.CreatedRefreshTokenHash);
        Assert.Equal(64, repository.CreatedRefreshTokenHash!.Length);
        Assert.Equal(now.UtcDateTime, repository.CreatedAtUtc);
        Assert.Equal(now.AddDays(30).UtcDateTime, repository.ExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshAsyncRotatesTokenAndIssuesNewAccessToken()
    {
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), ["admin"]));
        AuthSessionService service = CreateService(repository, DateTimeOffset.UnixEpoch);

        AuthTokenPair? result = await service.RefreshAsync("refresh-token-cu");

        Assert.NotNull(result);
        Assert.Equal("access-token", result.AccessToken);
        Assert.NotEqual("refresh-token-cu", result.RefreshToken);
        Assert.Equal(64, repository.CurrentRefreshTokenHash!.Length);
        Assert.Equal(64, repository.ReplacementRefreshTokenHash!.Length);
        Assert.NotEqual(repository.CurrentRefreshTokenHash, repository.ReplacementRefreshTokenHash);
    }

    [Fact]
    public async Task RefreshAsyncEmptyTokenDoesNotAccessPersistence()
    {
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), []));
        AuthSessionService service = CreateService(repository, DateTimeOffset.UtcNow);

        AuthTokenPair? result = await service.RefreshAsync(" ");

        Assert.Null(result);
        Assert.False(repository.RotateCalled);
    }

    [Fact]
    public async Task RevokeByRefreshTokenAsyncEmptyTokenIsIdempotent()
    {
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), []));
        AuthSessionService service = CreateService(repository, DateTimeOffset.UtcNow);

        await service.RevokeByRefreshTokenAsync(string.Empty);

        Assert.False(repository.RevokeCalled);
    }

    [Fact]
    public async Task CreateGoogleLoginTicketAsyncPersistsOnlyHashWithNinetySecondLifetime()
    {
        DateTimeOffset now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);
        RecordingAuthSessionRepository repository = new(new AuthSessionPrincipal(Guid.NewGuid(), []));
        AuthSessionService service = CreateService(repository, now);

        string? ticket = await service.CreateGoogleLoginTicketAsync(repository.Principal!.UserId);

        Assert.NotNull(ticket);
        Assert.NotEqual(ticket, repository.CreatedTicketHash);
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

        AuthTokenPair? result = await service.ExchangeGoogleLoginTicketAsync("google-ticket");

        Assert.NotNull(result);
        Assert.Equal("access-token", result.AccessToken);
        Assert.NotEqual(result.RefreshToken, repository.ExchangedRefreshTokenHash);
        Assert.Equal(64, repository.ExchangedTicketHash!.Length);
        Assert.Equal(64, repository.ExchangedRefreshTokenHash!.Length);
        Assert.Equal(now.UtcDateTime, repository.ExchangeAtUtc);
        Assert.Equal(now.AddDays(30).UtcDateTime, repository.ExchangeSessionExpiresAtUtc);
    }

    /// <summary>Repository ghi nhận tham số để test luật application mà không mô phỏng EF Core.</summary>
    private sealed class RecordingAuthSessionRepository(AuthSessionPrincipal? principal) : IAuthSessionRepository
    {
        public AuthSessionPrincipal? Principal { get; } = principal;
        public string? CreatedRefreshTokenHash { get; private set; }
        public DateTime CreatedAtUtc { get; private set; }
        public DateTime ExpiresAtUtc { get; private set; }
        public string? CurrentRefreshTokenHash { get; private set; }
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

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("AuthSessionService does not use generic CRUD reads.");

        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("AuthSessionService does not use generic CRUD reads.");

        public Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("AuthSessionService creates sessions through its atomic Auth operation.");

        public Task<bool> UpdateAsync(Session entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("AuthSessionService does not use generic CRUD updates.");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("AuthSessionService revokes sessions instead of deleting them.");

        public Task<AuthSessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken)
        {
            CreatedRefreshTokenHash = refreshTokenHash;
            CreatedAtUtc = utcNow;
            ExpiresAtUtc = expiresAtUtc;
            return Task.FromResult(Principal);
        }

        public Task<AuthSessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken)
        {
            RotateCalled = true;
            CurrentRefreshTokenHash = currentRefreshTokenHash;
            ReplacementRefreshTokenHash = replacementRefreshTokenHash;
            return Task.FromResult(Principal);
        }

        public Task<bool> CreateGoogleLoginTicketAsync(Guid userId, string ticketHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken)
        {
            CreatedTicketHash = ticketHash;
            TicketCreatedAtUtc = utcNow;
            TicketExpiresAtUtc = expiresAtUtc;
            return Task.FromResult(Principal?.UserId == userId);
        }

        public Task<AuthSessionPrincipal?> ConsumeGoogleLoginTicketAndCreateSessionAsync(
            string ticketHash,
            string refreshTokenHash,
            DateTime utcNow,
            DateTime sessionExpiresAtUtc,
            CancellationToken cancellationToken)
        {
            ExchangedTicketHash = ticketHash;
            ExchangedRefreshTokenHash = refreshTokenHash;
            ExchangeAtUtc = utcNow;
            ExchangeSessionExpiresAtUtc = sessionExpiresAtUtc;
            return Task.FromResult(Principal);
        }

        public Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken)
        {
            RevokeCalled = true;
            return Task.CompletedTask;
        }

        public Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid?> GetUserIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Principal?.UserId);
        }
    }

    private static AuthSessionService CreateService(RecordingAuthSessionRepository repository, DateTimeOffset now) =>
        new(repository, new FakeAccessTokenIssuer(), new FixedTimeProvider(now), new TestCorrelationIdProvider(), new TestAuditLogRepository());

    private sealed class TestCorrelationIdProvider : ICorrelationIdProvider
    {
        public string GetCorrelationId() => "test-correlation-id";
    }

    private sealed class TestAuditLogRepository : IAuditLogRepository
    {
        public Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Issuer cố định giúp assertion không phụ thuộc JWT cryptography.</summary>
    private sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(AccessTokenRequest request) => new("access-token", DateTimeOffset.UnixEpoch.AddMinutes(15));
    }

    /// <summary>TimeProvider cố định để kiểm tra chính xác hạn 30 ngày.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
