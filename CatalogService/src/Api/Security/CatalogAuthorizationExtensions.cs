using System.Security.Claims;

namespace Dprental.CatalogService.Api.Security;

/// <summary>Tên các authorization policy của CatalogService.</summary>
internal static class CatalogPolicies
{
    /// <summary>Chỉ Admin nền tảng được quản lý Canonical Catalog.</summary>
    internal const string CatalogAdmin = "CatalogAdmin";
}

/// <summary>Đăng ký authorization policy riêng của CatalogService.</summary>
internal static class CatalogAuthorizationExtensions
{
    // Alias role trong access token của AuthService (xem JwtClaimCatalog.RoleAliases bên AuthService).
    private const string RoleClaimType = "r";
    private const string AdminRoleAlias = "adm";
    private const string AdminPlatformRoleAlias = "adp";

    private const string TokenUseClaimType = "u";

    internal static IServiceCollection AddCatalogAuthorizationPolicies(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
            options.AddPolicy(CatalogPolicies.CatalogAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsPlatformAdmin(context.User))));

        return services;
    }

    /// <summary>
    /// User thật có role toàn cục Admin hoặc AdminPlatform.
    /// Access token của user không mang claim <c>u</c>; token có <c>u</c> (service token hay loại token
    /// chuyên dụng nào khác) đều bị từ chối, kể cả loại được thêm sau này.
    /// </summary>
    internal static bool IsPlatformAdmin(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return !user.HasClaim(claim => claim.Type == TokenUseClaimType)
            && (user.HasClaim(RoleClaimType, AdminRoleAlias) || user.HasClaim(RoleClaimType, AdminPlatformRoleAlias));
    }
}
