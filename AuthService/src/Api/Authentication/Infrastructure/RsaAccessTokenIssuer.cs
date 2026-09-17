using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.AuthService.Api.Authentication.Configuration;
using Dprental.AuthService.Application.Authentication.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Api.Authentication.Infrastructure;

internal sealed class RsaAccessTokenIssuer(
    IOptions<JwtOptions> options,
    RsaJwtKeyRing keyRing,
    TimeProvider timeProvider) : IAccessTokenIssuer
{
    private const string RoleClaimType = "role";

    public IssuedAccessToken Issue(AccessTokenRequest request)
    {
        if (request.SubjectId == Guid.Empty)
        {
            throw new ArgumentException("The token subject is required.", nameof(request));
        }

        DateTimeOffset issuedAtUtc = timeProvider.GetUtcNow();
        DateTimeOffset expiresAtUtc = issuedAtUtc.AddMinutes(options.Value.AccessTokenLifetimeMinutes);
        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, request.SubjectId.ToString("D")),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(issuedAtUtc.UtcDateTime).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        ];

        if (request.Roles is not null)
        {
            foreach (string role in request.Roles
                         .Where(role => !string.IsNullOrWhiteSpace(role))
                         .Select(role => role.Trim())
                         .Distinct(StringComparer.Ordinal))
            {
                claims.Add(new Claim(RoleClaimType, role));
            }
        }

        SigningCredentials signingCredentials = new(keyRing.ActiveSigningKey, SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            options.Value.Issuer,
            options.Value.Audience,
            claims,
            issuedAtUtc.UtcDateTime,
            expiresAtUtc.UtcDateTime,
            signingCredentials);

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
