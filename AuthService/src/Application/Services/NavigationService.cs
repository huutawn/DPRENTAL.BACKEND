using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Navigation;
using Dprental.AuthService.Domain.Enums;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

/// <summary>Resolve menu từ permission global và permission scoped của đúng organization được chọn.</summary>
public sealed class NavigationService(
    IUserRepository userRepository,
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    INavigationCatalogRepository navigationCatalogRepository) : INavigationService
{
    public async Task<Result<NavigationResponse>> GetNavigationAsync(
        Guid userId,
        Guid? organizationId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.User.NotFound);
        }

        if (organizationId is not Guid selectedOrganizationId || selectedOrganizationId == Guid.Empty)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.Navigation.InvalidOrganizationContext);
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.User.NotFound);
        }

        var membership = await membershipRepository.GetByOrganizationAndUserAsync(
            selectedOrganizationId,
            userId,
            cancellationToken);
        if (membership is null || membership.Status != MembershipStatus.Active)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
        }

        var organization = await organizationRepository.GetByIdAsync(selectedOrganizationId, cancellationToken);
        if (organization is null || !organization.IsActive)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
        }

        var catalog = await navigationCatalogRepository.GetOrganizationCatalogAsync(selectedOrganizationId, cancellationToken);
        if (catalog is null || catalog.DashboardType != organization.Type)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.Navigation.CatalogNotFound);
        }

        IReadOnlyList<string> effectivePermissions = await membershipRepository.GetEffectivePermissionsAsync(
            userId,
            selectedOrganizationId,
            cancellationToken);

        string[] identityPermissions = effectivePermissions
            .Where(PermissionCode.All.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new NavigationResponse(
            selectedOrganizationId,
            organization.Type.ToString(),
            identityPermissions,
            ResolveItems(catalog.Items, identityPermissions.ToHashSet(StringComparer.Ordinal)));
    }

    private static NavigationItemResponse[] ResolveItems(
        IEnumerable<Domain.Entities.NavigationItem> allItems,
        IReadOnlySet<string> effectivePermissions)
    {
        Domain.Entities.NavigationItem[] items = allItems.Where(item => item.IsActive).ToArray();
        return ResolveChildren(null, items, effectivePermissions, new HashSet<Guid>()).ToArray();
    }

    private static IEnumerable<NavigationItemResponse> ResolveChildren(
        Guid? parentId,
        IReadOnlyCollection<Domain.Entities.NavigationItem> allItems,
        IReadOnlySet<string> effectivePermissions,
        HashSet<Guid> ancestors)
    {
        foreach (var item in allItems.Where(candidate => candidate.ParentId == parentId)
                     .OrderBy(candidate => candidate.Order).ThenBy(candidate => candidate.Key, StringComparer.Ordinal))
        {
            if (ancestors.Contains(item.Id))
            {
                continue;
            }

            HashSet<Guid> nextAncestors = new(ancestors) { item.Id };
            NavigationItemResponse[] children = ResolveChildren(item.Id, allItems, effectivePermissions, nextAncestors).ToArray();
            bool hasPermission = item.Permissions.Any(binding => effectivePermissions.Contains(binding.Permission.Code));
            if (children.Length == 0 && !hasPermission)
            {
                continue;
            }

            yield return new NavigationItemResponse(item.Key, item.Label, item.Href, item.Icon, item.Order, children);
        }
    }
}
