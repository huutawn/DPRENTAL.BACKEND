using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Cài đặt logic trích xuất thông tin danh tính từ token JWT đã được xác thực an toàn.
/// Đảm bảo hỗ trợ các biến thể claim tiêu chuẩn và loại bỏ các giá trị không hợp lệ.
/// </summary>
public sealed class IdentityExtractor : IIdentityExtractor
{
    private static readonly string[] UserIdClaimTypes =
    [
        JwtRegisteredClaimNames.Sub,
        ClaimTypes.NameIdentifier,
        "uid",
        "userId"
    ];

    private static readonly string[] PartyIdClaimTypes =
    [
        "party_id",
        "PartyId",
        "partyId",
        "party"
    ];

    private static readonly string[] RoleClaimTypes =
    [
        "role",
        ClaimTypes.Role,
        "roles"
    ];

    private static readonly string[] PermissionClaimTypes =
    [
        "permissions",
        "permission",
        "scope",
        "scp"
    ];

    private static readonly char[] ClaimSeparators = [',', ' '];

    /// <inheritdoc />
    public IdentityContext Extract(ClaimsPrincipal? principal, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        string correlationId = httpContext.TraceIdentifier;
        if (string.IsNullOrWhiteSpace(correlationId) &&
            httpContext.Request.Headers.TryGetValue(GatewayHeaderConstants.CorrelationId, out var correlationHeader))
        {
            correlationId = correlationHeader.FirstOrDefault() ?? string.Empty;
        }

        if (principal?.Identity is null || !principal.Identity.IsAuthenticated)
        {
            return IdentityContext.Anonymous with { CorrelationId = correlationId };
        }

        // 1. Trích xuất UserId từ các claim tương ứng (ưu tiên "sub")
        string? userId = FindFirstClaimValue(principal, UserIdClaimTypes);
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Nếu không có thông tin user id hợp lệ trong token, không coi là danh tính hợp lệ để chuyển tiếp
            return IdentityContext.Anonymous with { CorrelationId = correlationId };
        }

        // 2. Trích xuất PartyId nếu có trong token
        string? partyId = FindFirstClaimValue(principal, PartyIdClaimTypes);

        // 3. Trích xuất toàn bộ Roles
        List<string> roles = new();
        foreach (string roleClaimType in RoleClaimTypes)
        {
            foreach (Claim claim in principal.FindAll(roleClaimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    string[] splitRoles = claim.Value.Split(ClaimSeparators, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string r in splitRoles)
                    {
                        string trimmed = r.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !roles.Contains(trimmed, StringComparer.Ordinal))
                        {
                            roles.Add(trimmed);
                        }
                    }
                }
            }
        }

        // 4. Trích xuất Permissions hoặc Scopes
        List<string> permissions = new();
        foreach (string permClaimType in PermissionClaimTypes)
        {
            foreach (Claim claim in principal.FindAll(permClaimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    string[] splitPerms = claim.Value.Split(ClaimSeparators, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in splitPerms)
                    {
                        string trimmed = p.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !permissions.Contains(trimmed, StringComparer.Ordinal))
                        {
                            permissions.Add(trimmed);
                        }
                    }
                }
            }
        }
        return new IdentityContext(
            userId: userId,
            partyId: partyId,
            roles: roles,
            permissions: permissions,
            correlationId: correlationId,
            isAuthenticated: true);
    }

    private static string? FindFirstClaimValue(ClaimsPrincipal principal, string[] claimTypes)
    {
        foreach (string claimType in claimTypes)
        {
            string? value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
