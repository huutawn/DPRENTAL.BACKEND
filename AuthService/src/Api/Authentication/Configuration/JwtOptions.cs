namespace Dprental.AuthService.Api.Authentication.Configuration;

internal sealed class JwtOptions
{
    internal const string SectionName = "Auth:Jwt";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public int AccessTokenLifetimeMinutes { get; init; }

    public string ActiveKeyId { get; init; } = string.Empty;

    public IReadOnlyList<JwtKeyOptions> Keys { get; init; } = [];
}

internal sealed class JwtKeyOptions
{
    public string KeyId { get; init; } = string.Empty;

    public string PublicKeyPem { get; init; } = string.Empty;

    public string? PrivateKeyPem { get; init; }
}
