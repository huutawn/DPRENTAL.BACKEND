using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Dprental.Gateway.Security;

/// <summary>
/// Cài đặt logic trích xuất thông tin danh tính từ token JWT đã được xác thực an toàn.
/// Đảm bảo hỗ trợ các biến thể claim tiêu chuẩn và loại bỏ các giá trị không hợp lệ.
/// </summary>
public sealed class IdentityExtractor
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

    private static readonly char[] ClaimSeparators = [',', ' '];

    /// <summary>Trích xuất danh tính chỉ từ các claim đã được xác thực.</summary>
    public IdentityContext Extract(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is null || !principal.Identity.IsAuthenticated)
        {
            return IdentityContext.Anonymous;
        }

        // 1. Trích xuất UserId từ các claim tương ứng (ưu tiên "sub")
        string? userId = FindFirstClaimValue(principal, UserIdClaimTypes);
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Nếu không có thông tin user id hợp lệ trong token, không coi là danh tính hợp lệ để chuyển tiếp
            return IdentityContext.Anonymous;
        }

        // 2. Trích xuất PartyId nếu có trong token
        string? partyId = FindFirstClaimValue(principal, PartyIdClaimTypes);

        return new IdentityContext(userId, partyId, ExtractRoles(principal), true);
    }

    private static List<string> ExtractRoles(ClaimsPrincipal principal)
    {
        List<string> roles = new();
        foreach (string claimType in RoleClaimTypes)
        {
            foreach (Claim claim in principal.FindAll(claimType))
            {
                foreach (string role in claim.Value.Split(ClaimSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!roles.Contains(role, StringComparer.Ordinal))
                    {
                        roles.Add(role);
                    }
                }
            }
        }

        return roles;
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
