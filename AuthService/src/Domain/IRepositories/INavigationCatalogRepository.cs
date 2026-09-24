using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>Persistence boundary cho template dashboard, catalog organization và permission bindings của menu.</summary>
public interface INavigationCatalogRepository
{
    Task<NavigationCatalog?> GetTemplateAsync(OrganizationType dashboardType, CancellationToken cancellationToken = default);
    Task<NavigationCatalog?> GetOrganizationCatalogAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Permission>> GetAssignablePermissionsAsync(CancellationToken cancellationToken = default);
    Task<Permission?> GetAssignablePermissionAsync(Guid permissionId, CancellationToken cancellationToken = default);
    Task<NavigationItem?> GetItemAsync(Guid catalogId, Guid itemId, CancellationToken cancellationToken = default);
    Task<bool> KeyExistsAsync(Guid catalogId, string key, Guid? excludingItemId = null, CancellationToken cancellationToken = default);
    Task AddItemAsync(NavigationItem item, CancellationToken cancellationToken = default);
    Task UpdateItemAsync(NavigationItem item, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(NavigationItem item, CancellationToken cancellationToken = default);
    Task AddPermissionAsync(NavigationItemPermission permission, CancellationToken cancellationToken = default);
    Task<bool> RemovePermissionAsync(Guid itemId, Guid permissionId, CancellationToken cancellationToken = default);
}
