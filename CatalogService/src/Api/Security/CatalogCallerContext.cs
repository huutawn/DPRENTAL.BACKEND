using System.Security.Claims;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common.Security;

namespace Dprental.CatalogService.Api.Security;

/// <summary>
/// Đọc danh tính caller từ <see cref="ClaimsPrincipal"/> của request hiện tại.
/// Không đọc các header X-User-Id, X-Role, X-Organization-Id: client có thể tự gửi chúng.
/// </summary>
public sealed class CatalogCallerContext(ClaimsPrincipal? user) : ICatalogCallerContext
{
    public CatalogCallerContext(IHttpContextAccessor httpContextAccessor)
        : this(httpContextAccessor.HttpContext?.User)
    {
    }

    public bool IsAuthenticated => user?.Identity?.IsAuthenticated == true;

    public bool IsService => IsAuthenticated && user!.HasClaim(CatalogClaims.Types.TokenUse, CatalogClaims.TokenUses.Service);

    public bool IsAdmin => IsAuthenticated && !IsService && Roles.Contains(CatalogClaims.Roles.Admin);

    public Guid? UserId =>
        IsAuthenticated
        && Guid.TryParse(user!.FindFirst(CatalogClaims.Types.Subject)?.Value, out Guid id)
        && id != Guid.Empty
            ? id
            : null;

    public IReadOnlySet<string> Roles => ReadValues(CatalogClaims.Types.Role);

    public IReadOnlySet<string> Permissions => ReadValues(CatalogClaims.Types.Permission);

    public IReadOnlySet<Guid> OrganizationIds
    {
        get
        {
            HashSet<Guid> organizationIds = [];
            foreach (string value in ReadValues(CatalogClaims.Types.Organization))
            {
                if (Guid.TryParse(value, out Guid id) && id != Guid.Empty)
                {
                    organizationIds.Add(id);
                }
            }

            return organizationIds;
        }
    }

    /// <summary>Alias role/permission phân biệt hoa thường, đúng như AuthService phát hành.</summary>
    private HashSet<string> ReadValues(string claimType) =>
        IsAuthenticated
            ? user!.FindAll(claimType)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal)
            : [];
}
