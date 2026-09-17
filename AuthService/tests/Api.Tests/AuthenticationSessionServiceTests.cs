using AuthService.Application.Authentication.Sessions;
using Dprental.AuthService.Application.Authentication.Tokens;
using Xunit;

namespace AuthService.Api.Tests;

/// <summary>Kiểm tra use case session mà không phụ thuộc Postgres hay private RSA key thật.</summary>
public sealed class AuthenticationSessionServiceTests
{
    [Fact]
    public async Task CreateSessionAsyncPersistsHashAndIssuesTokenPair()
    {
        DateTimeOffset now = new(2026, 9, 17, 7, 30, 0, TimeSpan.Zero);
        RecordingSessionStore store = new(new SessionPrincipal(Guid.NewGuid(), ["member"]));
        AuthenticationSessionService service = new(store, new FakeAccessTokenIssuer(), new FixedTimeProvider(now));

        AuthenticationTokenPair? result = await service.CreateSessionAsync(store.Principal!.UserId);

        Assert.NotNull(result);
        Assert.Equal("access-token", result.AccessToken);
        Assert.NotEqual(result.RefreshToken, store.CreatedRefreshTokenHash);
        Assert.Equal(64, store.CreatedRefreshTokenHash!.Length);
        Assert.Equal(now.UtcDateTime, store.CreatedAtUtc);
        Assert.Equal(now.AddDays(30).UtcDateTime, store.ExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshAsyncRotatesTokenAndIssuesNewAccessToken()
    {
        RecordingSessionStore store = new(new SessionPrincipal(Guid.NewGuid(), ["admin"]));
        AuthenticationSessionService service = new(store, new FakeAccessTokenIssuer(), new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        AuthenticationTokenPair? result = await service.RefreshAsync("refresh-token-cu");

        Assert.NotNull(result);
        Assert.Equal("access-token", result.AccessToken);
        Assert.NotEqual("refresh-token-cu", result.RefreshToken);
        Assert.Equal(64, store.CurrentRefreshTokenHash!.Length);
        Assert.Equal(64, store.ReplacementRefreshTokenHash!.Length);
        Assert.NotEqual(store.CurrentRefreshTokenHash, store.ReplacementRefreshTokenHash);
    }

    [Fact]
    public async Task RefreshAsyncEmptyTokenDoesNotAccessPersistence()
    {
        RecordingSessionStore store = new(new SessionPrincipal(Guid.NewGuid(), []));
        AuthenticationSessionService service = new(store, new FakeAccessTokenIssuer(), TimeProvider.System);

        AuthenticationTokenPair? result = await service.RefreshAsync(" ");

        Assert.Null(result);
        Assert.False(store.RotateCalled);
    }

    [Fact]
    public async Task RevokeByRefreshTokenAsyncEmptyTokenIsIdempotent()
    {
        RecordingSessionStore store = new(new SessionPrincipal(Guid.NewGuid(), []));
        AuthenticationSessionService service = new(store, new FakeAccessTokenIssuer(), TimeProvider.System);

        await service.RevokeByRefreshTokenAsync(string.Empty);

        Assert.False(store.RevokeCalled);
    }

    /// <summary>Store ghi nhận tham số để test luật application mà không mô phỏng EF Core.</summary>
    private sealed class RecordingSessionStore(SessionPrincipal? principal) : IAuthenticationSessionStore
    {
        public SessionPrincipal? Principal { get; } = principal;
        public string? CreatedRefreshTokenHash { get; private set; }
        public DateTime CreatedAtUtc { get; private set; }
        public DateTime ExpiresAtUtc { get; private set; }
        public string? CurrentRefreshTokenHash { get; private set; }
        public string? ReplacementRefreshTokenHash { get; private set; }
        public bool RotateCalled { get; private set; }
        public bool RevokeCalled { get; private set; }

        public Task<SessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken)
        {
            CreatedRefreshTokenHash = refreshTokenHash;
            CreatedAtUtc = utcNow;
            ExpiresAtUtc = expiresAtUtc;
            return Task.FromResult(Principal);
        }

        public Task<SessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken)
        {
            RotateCalled = true;
            CurrentRefreshTokenHash = currentRefreshTokenHash;
            ReplacementRefreshTokenHash = replacementRefreshTokenHash;
            return Task.FromResult(Principal);
        }

        public Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken)
        {
            RevokeCalled = true;
            return Task.CompletedTask;
        }
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
