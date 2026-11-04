using System.Security.Claims;

namespace Dprental.ReviewService.Application.Security;

/// <summary>
/// Quy tắc bảo mật tập trung chuẩn hóa xác thực và phân quyền cho ReviewService theo Auth Contract.
/// </summary>
public static class ReviewSecurityRules
{
    public const string AdminRoleShort = "adm";
    public const string AdminRoleFull = "Admin";
    public const string ServiceUserType = "svc";

    /// <summary>
    /// Kiểm tra người dùng có vai trò quản trị viên (Admin) hợp lệ hay không:
    /// 1. Yêu cầu principal đã được xác thực (authenticated).
    /// 2. Từ chối service tokens (u = "svc").
    /// 3. Từ chối claim boolean giả mạo 'adm = true' (boolean alone never grants admin).
    /// 4. Kiểm tra claim chuẩn: (c.Type == "r" || c.Type == ClaimTypes.Role) &amp;&amp; (c.Value == "adm" || c.Value == "Admin")
    ///    hoặc principal.IsInRole("adm") hoặc principal.IsInRole("Admin").
    /// </summary>
    public static bool IsAdmin(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        // 1. Từ chối service token (u = "svc")
        var userType = principal.FindFirst("u")?.Value;
        if (string.Equals(userType, ServiceUserType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Reject forged boolean claim 'adm = true' và kiểm tra role hợp lệ.
        // Chỉ chấp nhận claim role chuẩn ("r" hoặc ClaimTypes.Role) mang giá trị "adm" hoặc "Admin".
        bool hasAdminRole = principal.IsInRole(AdminRoleShort)
            || principal.IsInRole(AdminRoleFull)
            || principal.Claims.Any(c =>
                (string.Equals(c.Type, "r", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(c.Value, AdminRoleShort, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, AdminRoleFull, StringComparison.OrdinalIgnoreCase)));

        return hasAdminRole;
    }
}
