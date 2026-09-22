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
    IOrganizationMembershipRepository membershipRepository) : INavigationService
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

        if (organizationId == Guid.Empty)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.Navigation.InvalidOrganizationContext);
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure<NavigationResponse>(AuthErrors.User.NotFound);
        }

        IReadOnlyList<string> effectivePermissions;
        if (organizationId is Guid selectedOrganizationId)
        {
            var membership = await membershipRepository.GetByOrganizationAndUserAsync(
                selectedOrganizationId,
                userId,
                cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure<NavigationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            var organization = await organizationRepository.GetByIdAsync(selectedOrganizationId, cancellationToken);
            if (organization is null || organization.Status != OrganizationStatus.Active)
            {
                return Result.Failure<NavigationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            effectivePermissions = await membershipRepository.GetEffectivePermissionsAsync(
                userId,
                selectedOrganizationId,
                cancellationToken);
        }
        else
        {
            effectivePermissions = await membershipRepository.GetGlobalPermissionsAsync(userId, cancellationToken);
        }

        string[] identityPermissions = effectivePermissions
            .Where(PermissionCode.All.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new NavigationResponse(
            organizationId,
            identityPermissions,
            NavigationCatalog.Resolve(identityPermissions.ToHashSet(StringComparer.Ordinal)));
    }
}
