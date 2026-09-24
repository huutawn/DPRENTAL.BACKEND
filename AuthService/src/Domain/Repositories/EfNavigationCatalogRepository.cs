using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Enums;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfNavigationCatalogRepository(AuthDbContext dbContext) : INavigationCatalogRepository
{
    public Task<NavigationCatalog?> GetTemplateAsync(OrganizationType dashboardType, CancellationToken cancellationToken = default) =>
        CatalogQuery().SingleOrDefaultAsync(catalog => catalog.OrganizationId == null && catalog.DashboardType == dashboardType, cancellationToken);

    public Task<NavigationCatalog?> GetOrganizationCatalogAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        CatalogQuery().SingleOrDefaultAsync(catalog => catalog.OrganizationId == organizationId, cancellationToken);

    public async Task<IReadOnlyList<Permission>> GetAssignablePermissionsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Permissions
            .AsNoTracking()
            .Where(permission => PermissionCode.All.Contains(permission.Code))
            .OrderBy(permission => permission.Code)
            .ToListAsync(cancellationToken);

    public Task<Permission?> GetAssignablePermissionAsync(Guid permissionId, CancellationToken cancellationToken = default) =>
        dbContext.Permissions
            .AsNoTracking()
            .SingleOrDefaultAsync(permission => permission.Id == permissionId && PermissionCode.All.Contains(permission.Code), cancellationToken);

    public Task<NavigationItem?> GetItemAsync(Guid catalogId, Guid itemId, CancellationToken cancellationToken = default) =>
        dbContext.NavigationItems
            .Include(item => item.Permissions)
                .ThenInclude(binding => binding.Permission)
            .Include(item => item.Children)
            .SingleOrDefaultAsync(item => item.CatalogId == catalogId && item.Id == itemId, cancellationToken);

    public Task<bool> KeyExistsAsync(Guid catalogId, string key, Guid? excludingItemId = null, CancellationToken cancellationToken = default) =>
        dbContext.NavigationItems
            .AsNoTracking()
            .AnyAsync(item => item.CatalogId == catalogId && item.Key == key && (excludingItemId == null || item.Id != excludingItemId), cancellationToken);

    public async Task AddItemAsync(NavigationItem item, CancellationToken cancellationToken = default)
    {
        dbContext.NavigationItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateItemAsync(NavigationItem item, CancellationToken cancellationToken = default)
    {
        dbContext.NavigationItems.Update(item);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteItemAsync(NavigationItem item, CancellationToken cancellationToken = default)
    {
        dbContext.NavigationItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddPermissionAsync(NavigationItemPermission permission, CancellationToken cancellationToken = default)
    {
        dbContext.NavigationItemPermissions.Add(permission);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemovePermissionAsync(Guid itemId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        NavigationItemPermission? binding = await dbContext.NavigationItemPermissions
            .SingleOrDefaultAsync(item => item.NavigationItemId == itemId && item.PermissionId == permissionId, cancellationToken);
        if (binding is null)
        {
            return false;
        }

        dbContext.NavigationItemPermissions.Remove(binding);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private IQueryable<NavigationCatalog> CatalogQuery() => dbContext.NavigationCatalogs
        .AsNoTracking()
        .Include(catalog => catalog.Items)
            .ThenInclude(item => item.Permissions)
                .ThenInclude(binding => binding.Permission);
}
