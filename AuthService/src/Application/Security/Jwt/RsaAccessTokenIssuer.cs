using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Application.Security.Jwt;

/// <summary>Hiện thực phát hành access token bằng private RSA key của key ring.</summary>
public sealed class RsaAccessTokenIssuer(
    IOptions<JwtOptions> options,
    RsaJwtKeyRing keyRing,
    TimeProvider timeProvider) : IAccessTokenIssuer
{
    /// <summary>Tên claim role ổn định, tương thích trực tiếp với JWT Bearer authorization.</summary>
    private const string RoleClaimType = "role";

    /// <summary>Tên claim permission dùng trong <c>RequireClaim("perm", code)</c>.</summary>
    private const string PermissionClaimType = "perm";

    /// <summary>Tên claim organization context — một giá trị cho mỗi organization user đang active.</summary>
    private const string OrganizationClaimType = "org";

    /// <summary>Phân biệt token của user thật (không có claim này) và token của service (client_credentials).</summary>
    private const string TokenUseClaimType = "token_use";

    /// <summary>Tên claim scope, dùng cho service token.</summary>
    private const string ScopeClaimType = "scope";

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

        AddDistinctClaims(claims, RoleClaimType, request.Roles);
        AddDistinctClaims(claims, PermissionClaimType, request.Permissions);
        AddDistinctClaims(claims, ScopeClaimType, request.Scopes);

        if (request.OrganizationIds is not null)
        {
            foreach (Guid organizationId in request.OrganizationIds.Distinct())
            {
                claims.Add(new Claim(OrganizationClaimType, organizationId.ToString("D")));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.TokenUse))
        {
            claims.Add(new Claim(TokenUseClaimType, request.TokenUse));
        }

        // SecurityKey mang ActiveKeyId nên JwtSecurityTokenHandler sẽ đưa kid vào JWT header.
        SigningCredentials signingCredentials = new(keyRing.ActiveSigningKey, SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            options.Value.Issuer,
            request.Audience ?? options.Value.Audience,
            claims,
            issuedAtUtc.UtcDateTime,
            expiresAtUtc.UtcDateTime,
            signingCredentials);

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    /// <summary>Thêm một claim theo mỗi giá trị không rỗng, đã trim và loại trùng.</summary>
    private static void AddDistinctClaims(List<Claim> claims, string claimType, IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (string value in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            claims.Add(new Claim(claimType, value));
        }
    }
}
