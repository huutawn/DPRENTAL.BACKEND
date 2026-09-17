using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.AuthService.Api.Authentication.Configuration;
using Dprental.AuthService.Application.Authentication.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Api.Authentication.Infrastructure;

/// <summary>Hiện thực phát hành access token bằng private RSA key của key ring.</summary>
internal sealed class RsaAccessTokenIssuer(
    IOptions<JwtOptions> options,
    RsaJwtKeyRing keyRing,
    TimeProvider timeProvider) : IAccessTokenIssuer
{
    /// <summary>Tên claim role ổn định, tương thích trực tiếp với JWT Bearer authorization.</summary>
    private const string RoleClaimType = "role";

    /// <summary>Tạo access token RS256 với các standard claim và roles đã chuẩn hóa.</summary>
    public IssuedAccessToken Issue(AccessTokenRequest request)
    {
        if (request.SubjectId == Guid.Empty)
        {
            throw new ArgumentException("The token subject is required.", nameof(request));
        }

        // Dùng TimeProvider để thời gian phát hành có thể kiểm thử được và nhất quán.
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
            // Không phát role rỗng, loại khoảng trắng thừa và tránh lặp claim role.
            foreach (string role in request.Roles
                         .Where(role => !string.IsNullOrWhiteSpace(role))
                         .Select(role => role.Trim())
                         .Distinct(StringComparer.Ordinal))
            {
                claims.Add(new Claim(RoleClaimType, role));
            }
        }

        // SecurityKey mang ActiveKeyId nên JwtSecurityTokenHandler sẽ đưa kid vào JWT header.
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
