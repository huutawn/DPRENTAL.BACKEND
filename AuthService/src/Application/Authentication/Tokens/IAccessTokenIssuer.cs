namespace Dprental.AuthService.Application.Authentication.Tokens;

/// <summary>
/// Issues short-lived access tokens for an already authenticated subject.
/// Authentication of the subject remains the responsibility of a use case.
/// </summary>
public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}

/// <summary>
/// Claims which do not require the domain User entity to issue an access token.
/// </summary>
public sealed record AccessTokenRequest(Guid SubjectId, IReadOnlyCollection<string>? Roles = null);

/// <summary>
/// A signed access token and its UTC expiry, for returning to a client after authentication.
/// </summary>
public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAtUtc);
