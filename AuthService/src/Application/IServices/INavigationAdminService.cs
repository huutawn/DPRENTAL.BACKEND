using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Navigation;
using Dprental.AuthService.Domain.Enums;

namespace Dprental.AuthService.Application.IServices;

public interface INavigationAdminService
{
    Task<Result<IReadOnlyCollection<NavigationAdminItemResponse>>> GetTemplateItemsAsync(Guid actorUserId, OrganizationType dashboardType, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyCollection<NavigationAdminItemResponse>>> GetOrganizationItemsAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyCollection<NavigationPermissionResponse>>> GetAssignablePermissionsAsync(Guid actorUserId, CancellationToken cancellationToken = default);
    Task<Result<NavigationAdminItemResponse>> CreateTemplateItemAsync(Guid actorUserId, OrganizationType dashboardType, CreateNavigationItemRequest request, CancellationToken cancellationToken = default);
    Task<Result<NavigationAdminItemResponse>> CreateOrganizationItemAsync(Guid actorUserId, Guid organizationId, CreateNavigationItemRequest request, CancellationToken cancellationToken = default);
    Task<Result<NavigationAdminItemResponse>> UpdateTemplateItemAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken = default);
    Task<Result<NavigationAdminItemResponse>> UpdateOrganizationItemAsync(Guid actorUserId, Guid organizationId, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken = default);
    Task<Result> DeleteTemplateItemAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, CancellationToken cancellationToken = default);
    Task<Result> DeleteOrganizationItemAsync(Guid actorUserId, Guid organizationId, Guid itemId, CancellationToken cancellationToken = default);
    Task<Result> AddTemplatePermissionAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default);
    Task<Result> AddOrganizationPermissionAsync(Guid actorUserId, Guid organizationId, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default);
    Task<Result> RemoveTemplatePermissionAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default);
    Task<Result> RemoveOrganizationPermissionAsync(Guid actorUserId, Guid organizationId, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default);
}
