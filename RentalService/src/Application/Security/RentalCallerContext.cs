using System.Collections.Frozen;
using System.Security.Claims;
using System.Text.Json;
using Dprental.RentalService.Application.Security.Jwt;

namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Immutable runtime implementation of IRentalCallerContext.
/// Enforces DPRental AuthService token contract specifications:
/// - Service tokens carry a valid UUID in 'sub' and 'u=svc'. For service callers, UserId is always null.
/// - Interactive user tokens must carry a valid non-empty UUID in 'sub'.
/// - Malformed or missing subject claims invalidate caller authentication (never inferred as service).
/// - Role and permission aliases (adm, adp, cus, own, mem) are mapped into immutable frozen sets.
/// </summary>
public sealed class RentalCallerContext : IRentalCallerContext
{
    private static readonly FrozenSet<string> EmptySet = FrozenSet<string>.Empty;

    private static readonly FrozenSet<string> AdminRoleTokens = new[]
    {
        RentalAuthClaims.StandardRoles.Admin,
        RentalAuthClaims.RoleAliases.Admin,
        RentalAuthClaims.StandardRoles.AdminPlatform,
        RentalAuthClaims.RoleAliases.AdminPlatform
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool IsAuthenticated { get; }
    public bool IsService { get; }
    public Guid? UserId { get; }
    public bool IsProfileCompleted { get; }
    public IReadOnlySet<string> Roles { get; }
    public IReadOnlySet<string> Permissions { get; }
    public string? Username { get; }

    internal RentalCallerContext(
        bool isAuthenticated,
        bool isService,
        Guid? userId,
        bool isProfileCompleted,
        FrozenSet<string>? roles = null,
        FrozenSet<string>? permissions = null,
        string? username = null)
    {
        IsAuthenticated = isAuthenticated;
        IsService = isService;
        UserId = userId;
        IsProfileCompleted = isProfileCompleted;
        Roles = roles ?? EmptySet;
        Permissions = permissions ?? EmptySet;
        Username = username;
    }

    public static RentalCallerContext Anonymous =>
        new RentalCallerContext(
            isAuthenticated: false,
            isService: false,
            userId: null,
            isProfileCompleted: false,
            roles: EmptySet,
            permissions: EmptySet,
            username: null);

    public static RentalCallerContext FromClaimsPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return Anonymous;
        }

        var claims = principal.Claims.Select(c => new KeyValuePair<string, string>(c.Type, c.Value));
        return FromClaims(claims);
    }

    public static RentalCallerContext FromClaims(IEnumerable<KeyValuePair<string, string>>? claims)
    {
        if (claims is null)
        {
            return Anonymous;
        }

        var claimList = claims.ToList();
        if (claimList.Count == 0)
        {
            return Anonymous;
        }

        string? sub = claimList.FirstOrDefault(c => c.Key == RentalAuthClaims.Subject || c.Key == ClaimTypes.NameIdentifier || c.Key == "sub").Value;
        string? pcStr = claimList.FirstOrDefault(c => c.Key == RentalAuthClaims.ProfileCompleted).Value;
        string? u = claimList.FirstOrDefault(c => c.Key == RentalAuthClaims.UserTypeOrAlias).Value;
        string? username = claimList.FirstOrDefault(c => c.Key == RentalAuthClaims.Username || c.Key == "username" || c.Key == "name").Value;
        HashSet<string> roles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> permissions = new(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in claimList)
        {
            if (claim.Key == RentalAuthClaims.Roles)
            {
                AddDelimitedOrSingle(roles, claim.Value);
            }
            else if (claim.Key == RentalAuthClaims.Permissions)
            {
                AddDelimitedOrSingle(permissions, claim.Value);
            }
        }

        // Expand aliases into canonical roles and vice-versa
        NormalizeRoleAliases(roles);

        FrozenSet<string> frozenRoles = roles.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        FrozenSet<string> frozenPermissions = permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out Guid parsedSubId) || parsedSubId == Guid.Empty)
        {
            return Anonymous;
        }

        bool isService = string.Equals(u, RentalAuthClaims.ServiceIndicator, StringComparison.OrdinalIgnoreCase);

        if (isService)
        {
            return new RentalCallerContext(
                isAuthenticated: true,
                isService: true,
                userId: null,
                isProfileCompleted: false,
                roles: frozenRoles,
                permissions: frozenPermissions);
        }

        bool isProfileCompleted = bool.TryParse(pcStr, out bool pcBool) && pcBool;

        return new RentalCallerContext(
            isAuthenticated: true,
            isService: false,
            userId: parsedSubId,
            isProfileCompleted: isProfileCompleted,
            roles: frozenRoles,
            permissions: frozenPermissions);
    }
    public static RentalCallerContext ForUser(
        Guid userId,
        bool isProfileCompleted = true,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        string? username = null)
    {
        var roleSet = roles != null ? new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase) : [];
        NormalizeRoleAliases(roleSet);
        var permSet = permissions != null ? permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase) : EmptySet;
        return new RentalCallerContext(
            isAuthenticated: true,
            isService: false,
            userId: userId,
            isProfileCompleted: isProfileCompleted,
            roles: roleSet.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            permissions: permSet,
            username: username);
    }

    public static RentalCallerContext ForService(
        string serviceName,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null)
    {
        var roleSet = roles != null ? new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase) : [];
        roleSet.Add("service");
        NormalizeRoleAliases(roleSet);
        var permSet = permissions != null ? permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase) : EmptySet;
        return new RentalCallerContext(
            isAuthenticated: true,
            isService: true,
            userId: null,
            isProfileCompleted: false,
            roles: roleSet.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            permissions: permSet,
            username: serviceName);
    }

    private static void NormalizeRoleAliases(HashSet<string> roles)
    {
        if (roles.Contains(RentalAuthClaims.RoleAliases.Admin))
            roles.Add(RentalAuthClaims.StandardRoles.Admin);
        if (roles.Contains(RentalAuthClaims.StandardRoles.Admin))
            roles.Add(RentalAuthClaims.RoleAliases.Admin);

        if (roles.Contains(RentalAuthClaims.RoleAliases.AdminPlatform))
            roles.Add(RentalAuthClaims.StandardRoles.AdminPlatform);
        if (roles.Contains(RentalAuthClaims.StandardRoles.AdminPlatform))
            roles.Add(RentalAuthClaims.RoleAliases.AdminPlatform);

        if (roles.Contains(RentalAuthClaims.RoleAliases.Customer))
            roles.Add(RentalAuthClaims.StandardRoles.Customer);
        if (roles.Contains(RentalAuthClaims.StandardRoles.Customer))
            roles.Add(RentalAuthClaims.RoleAliases.Customer);

        if (roles.Contains(RentalAuthClaims.RoleAliases.Owner))
            roles.Add(RentalAuthClaims.StandardRoles.Owner);
        if (roles.Contains(RentalAuthClaims.StandardRoles.Owner))
            roles.Add(RentalAuthClaims.RoleAliases.Owner);

        if (roles.Contains(RentalAuthClaims.RoleAliases.Member))
            roles.Add(RentalAuthClaims.StandardRoles.Member);
        if (roles.Contains(RentalAuthClaims.StandardRoles.Member))
            roles.Add(RentalAuthClaims.RoleAliases.Member);
    }

    private static void AddDelimitedOrSingle(HashSet<string> set, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return;

        if (rawValue.TrimStart().StartsWith('[') && rawValue.TrimEnd().EndsWith(']'))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(rawValue);
                if (list is not null)
                {
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item)) set.Add(item.Trim());
                    }
                    return;
                }
            }
            catch
            {
                // Fallback to comma-separated
            }
        }

        foreach (var part in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part);
        }
    }

    public bool HasRole(string role) =>
        !string.IsNullOrWhiteSpace(role) && Roles.Contains(role);

    public bool HasPermission(string permission) =>
        !string.IsNullOrWhiteSpace(permission) && Permissions.Contains(permission);

    public bool IsAdmin() =>
        Roles.Overlaps(AdminRoleTokens);
}
