using Dprental.AuthService.Api.Authentication.Infrastructure;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Api.Authentication.Configuration;

internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    private const int MaximumAccessTokenLifetimeMinutes = 60;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Auth:Jwt:Issuer must be configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Auth:Jwt:Audience must be configured.");
        }

        if (options.AccessTokenLifetimeMinutes is < 1 or > MaximumAccessTokenLifetimeMinutes)
        {
            failures.Add($"Auth:Jwt:AccessTokenLifetimeMinutes must be between 1 and {MaximumAccessTokenLifetimeMinutes}.");
        }

        if (string.IsNullOrWhiteSpace(options.ActiveKeyId))
        {
            failures.Add("Auth:Jwt:ActiveKeyId must be configured.");
        }

        if (options.Keys.Count == 0)
        {
            failures.Add("Auth:Jwt:Keys must contain at least one verification key.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        try
        {
            using RsaJwtKeyRing _ = new(options);
        }
        catch (ArgumentException exception)
        {
            failures.Add(exception.Message);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            failures.Add($"Auth:Jwt:Keys contains an invalid PEM key: {exception.Message}");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
