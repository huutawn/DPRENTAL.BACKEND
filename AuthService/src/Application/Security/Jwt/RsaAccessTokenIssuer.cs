using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Application.DTOs.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Application.Security.Jwt;

/// <summary>Hiện thực phát hành access token bằng private RSA key của key ring với claims rút gọn chuẩn.</summary>
public sealed class RsaAccessTokenIssuer(
    IOptions<JwtOptions> options,
    RsaJwtKeyRing keyRing,
    TimeProvider timeProvider) : IAccessTokenIssuer
{
    /// <summary>Tên claim role rút gọn thành "r".</summary>
    private const string RoleClaimType = JwtClaimCatalog.Claims.Role;

    /// <summary>Tên claim permission rút gọn thành "p".</summary>
    private const string PermissionClaimType = JwtClaimCatalog.Claims.Permission;

    /// <summary>Tên claim organization context rút gọn thành "o".</summary>
    private const string OrganizationClaimType = JwtClaimCatalog.Claims.Organization;

    /// <summary>Tên claim token use rút gọn thành "u".</summary>
    private const string TokenUseClaimType = JwtClaimCatalog.Claims.TokenUse;

    /// <summary>Tên claim scope rút gọn thành "s".</summary>
    private const string ScopeClaimType = JwtClaimCatalog.Claims.Scope;

    /// <summary>Tên claim hồ sơ đã hoàn thiện rút gọn thành "pc".</summary>
    private const string ProfileCompletedClaimType = JwtClaimCatalog.Claims.ProfileCompleted;

    /// <summary>Tạo access token RS256 với các standard claim và alias đã chuẩn hóa.</summary>
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

        // Ánh xạ role và permission sang alias ngắn từ catalog duy nhất sau khi đã trim
        IReadOnlyList<string>? roleAliases = request.Roles?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => JwtClaimCatalog.RoleAliases.ToAlias(r.Trim()))
            .ToArray();
        IReadOnlyList<string>? permissionAliases = request.Permissions?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => JwtClaimCatalog.PermissionAliases.ToAlias(p.Trim()))
            .ToArray();

        AddDistinctClaims(claims, RoleClaimType, roleAliases);
        AddDistinctClaims(claims, PermissionClaimType, permissionAliases);
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
            string tokenUseValue = string.Equals(request.TokenUse, "service", StringComparison.OrdinalIgnoreCase)
                ? JwtClaimCatalog.TokenUses.Service
                : request.TokenUse.Trim();
            claims.Add(new Claim(TokenUseClaimType, tokenUseValue));
        }

        // Service token (không có hồ sơ user) không mang claim này — request.IsProfileCompleted vẫn null.
        if (request.IsProfileCompleted.HasValue)
        {
            claims.Add(new Claim(
                ProfileCompletedClaimType,
                request.IsProfileCompleted.Value ? "true" : "false",
                ClaimValueTypes.Boolean));
        }

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
