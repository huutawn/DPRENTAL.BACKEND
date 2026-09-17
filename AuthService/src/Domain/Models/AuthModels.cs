namespace Dprental.AuthService.Domain.Models;

/// <summary>Minimal authenticated principal data used by the Auth session flow.</summary>
public sealed record AuthSessionPrincipal(Guid UserId, IReadOnlyCollection<string> Roles);

/// <summary>Claims required to issue an access token.</summary>
public sealed record AccessTokenRequest(Guid SubjectId, IReadOnlyCollection<string>? Roles = null);

/// <summary>A signed access token and its expiry.</summary>
public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAtUtc);

/// <summary>The access and refresh tokens returned by the Auth session flow.</summary>
public sealed record AuthTokenPair(string AccessToken, string RefreshToken);
