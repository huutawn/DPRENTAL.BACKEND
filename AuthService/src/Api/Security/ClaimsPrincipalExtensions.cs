using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Api.Security;

/// <summary>Đọc thông tin chủ thể từ access token đã được middleware xác thực (chữ ký, hạn, audience).</summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Id user từ claim <c>sub</c>, hay <c>null</c> nếu chưa xác thực, thiếu/sai định dạng claim,
    /// hoặc token thuộc về một service (<c>token_use = svc</c>) chứ không phải user thật.
    /// Chỉ đọc claim, không tự kiểm tra chữ ký hay hạn token — việc đó thuộc middleware xác thực chạy trước.
    /// </summary>
    internal static Guid? GetJwtSub(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (user.HasClaim(JwtClaimCatalog.Claims.TokenUse, JwtClaimCatalog.TokenUses.Service))
        {
            return null;
        }

        string? sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(sub, out Guid userId) && userId != Guid.Empty ? userId : null;
    }
}
