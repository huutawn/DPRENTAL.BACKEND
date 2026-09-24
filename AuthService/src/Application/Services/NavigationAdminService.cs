using System.Text.RegularExpressions;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Navigation;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

/// <summary>Quản trị template và catalog navigation bằng permission global identity.navigation.manage.</summary>
public sealed class NavigationAdminService(
    INavigationCatalogRepository navigationCatalogRepository,
    IOrganizationRepository organizationRepository,
    IRoleRepository roleRepository,
    IUserRepository userRepository) : INavigationAdminService
{
    public Task<Result<IReadOnlyCollection<NavigationAdminItemResponse>>> GetTemplateItemsAsync(Guid actorUserId, OrganizationType dashboardType, CancellationToken cancellationToken = default) =>
        GetItemsAsync(actorUserId, GetTemplateCatalogAsync(dashboardType, cancellationToken), cancellationToken);

    public Task<Result<IReadOnlyCollection<NavigationAdminItemResponse>>> GetOrganizationItemsAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default) =>
        GetItemsAsync(actorUserId, GetOrganizationCatalogAsync(organizationId, cancellationToken), cancellationToken);

    public async Task<Result<IReadOnlyCollection<NavigationPermissionResponse>>> GetAssignablePermissionsAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<NavigationPermissionResponse>>(guard.Error);
        }

        IReadOnlyCollection<NavigationPermissionResponse> permissions = (await navigationCatalogRepository.GetAssignablePermissionsAsync(cancellationToken))
            .Select(permission => new NavigationPermissionResponse(permission.Id, permission.Code, permission.Description))
            .ToArray();
        return Result.Success(permissions);
    }

    public Task<Result<NavigationAdminItemResponse>> CreateTemplateItemAsync(Guid actorUserId, OrganizationType dashboardType, CreateNavigationItemRequest request, CancellationToken cancellationToken = default) =>
        CreateItemAsync(actorUserId, GetTemplateCatalogAsync(dashboardType, cancellationToken), request, cancellationToken);

    public Task<Result<NavigationAdminItemResponse>> CreateOrganizationItemAsync(Guid actorUserId, Guid organizationId, CreateNavigationItemRequest request, CancellationToken cancellationToken = default) =>
        CreateItemAsync(actorUserId, GetOrganizationCatalogAsync(organizationId, cancellationToken), request, cancellationToken);

    public Task<Result<NavigationAdminItemResponse>> UpdateTemplateItemAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken = default) =>
        UpdateItemAsync(actorUserId, GetTemplateCatalogAsync(dashboardType, cancellationToken), itemId, request, cancellationToken);

    public Task<Result<NavigationAdminItemResponse>> UpdateOrganizationItemAsync(Guid actorUserId, Guid organizationId, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken = default) =>
        UpdateItemAsync(actorUserId, GetOrganizationCatalogAsync(organizationId, cancellationToken), itemId, request, cancellationToken);

    public Task<Result> DeleteTemplateItemAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, CancellationToken cancellationToken = default) =>
        DeleteItemAsync(actorUserId, GetTemplateCatalogAsync(dashboardType, cancellationToken), itemId, cancellationToken);

    public Task<Result> DeleteOrganizationItemAsync(Guid actorUserId, Guid organizationId, Guid itemId, CancellationToken cancellationToken = default) =>
        DeleteItemAsync(actorUserId, GetOrganizationCatalogAsync(organizationId, cancellationToken), itemId, cancellationToken);

    public Task<Result> AddTemplatePermissionAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default) =>
        AddPermissionAsync(actorUserId, GetTemplateCatalogAsync(dashboardType, cancellationToken), itemId, permissionId, cancellationToken);

    public Task<Result> AddOrganizationPermissionAsync(Guid actorUserId, Guid organizationId, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default) =>
        AddPermissionAsync(actorUserId, GetOrganizationCatalogAsync(organizationId, cancellationToken), itemId, permissionId, cancellationToken);

    public Task<Result> RemoveTemplatePermissionAsync(Guid actorUserId, OrganizationType dashboardType, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default) =>
        RemovePermissionAsync(actorUserId, GetTemplateCatalogAsync(dashboardType, cancellationToken), itemId, permissionId, cancellationToken);

    public Task<Result> RemoveOrganizationPermissionAsync(Guid actorUserId, Guid organizationId, Guid itemId, Guid permissionId, CancellationToken cancellationToken = default) =>
        RemovePermissionAsync(actorUserId, GetOrganizationCatalogAsync(organizationId, cancellationToken), itemId, permissionId, cancellationToken);

    private async Task<Result<IReadOnlyCollection<NavigationAdminItemResponse>>> GetItemsAsync(Guid actorUserId, Task<Result<NavigationCatalog>> catalogTask, CancellationToken cancellationToken)
    {
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<NavigationAdminItemResponse>>(guard.Error);
        }

        Result<NavigationCatalog> catalogResult = await catalogTask;
        if (catalogResult.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<NavigationAdminItemResponse>>(catalogResult.Error);
        }

        return catalogResult.Value.Items
            .OrderBy(item => item.Order).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => ToResponse(catalogResult.Value, item)).ToArray();
    }

    private async Task<Result<NavigationAdminItemResponse>> CreateItemAsync(Guid actorUserId, Task<Result<NavigationCatalog>> catalogTask, CreateNavigationItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure) return Result.Failure<NavigationAdminItemResponse>(guard.Error);
        Result<NavigationCatalog> catalogResult = await catalogTask;
        if (catalogResult.IsFailure) return Result.Failure<NavigationAdminItemResponse>(catalogResult.Error);
        NavigationCatalog catalog = catalogResult.Value;
        Result validation = await ValidateCreateAsync(catalog, request, cancellationToken);
        if (validation.IsFailure) return Result.Failure<NavigationAdminItemResponse>(validation.Error);

        NavigationItem item = new()
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            ParentId = request.ParentId,
            Key = request.Key.Trim(),
            Label = request.Label.Trim(),
            Href = NormalizeHref(request.Href),
            Icon = request.Icon.Trim(),
            Order = request.Order,
            IsActive = true
        };
        foreach (Guid permissionId in request.PermissionIds ?? [])
        {
            item.Permissions.Add(new NavigationItemPermission { NavigationItemId = item.Id, PermissionId = permissionId });
        }
        await navigationCatalogRepository.AddItemAsync(item, cancellationToken);
        NavigationItem? created = await navigationCatalogRepository.GetItemAsync(catalog.Id, item.Id, cancellationToken);
        return created is null
            ? Result.Failure<NavigationAdminItemResponse>(AuthErrors.Navigation.CatalogNotFound)
            : ToResponse(catalog, created);
    }

    private async Task<Result<NavigationAdminItemResponse>> UpdateItemAsync(Guid actorUserId, Task<Result<NavigationCatalog>> catalogTask, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure) return Result.Failure<NavigationAdminItemResponse>(guard.Error);
        Result<NavigationCatalog> catalogResult = await catalogTask;
        if (catalogResult.IsFailure) return Result.Failure<NavigationAdminItemResponse>(catalogResult.Error);
        NavigationCatalog catalog = catalogResult.Value;
        NavigationItem? item = await navigationCatalogRepository.GetItemAsync(catalog.Id, itemId, cancellationToken);
        if (item is null) return Result.Failure<NavigationAdminItemResponse>(AuthErrors.Navigation.CatalogNotFound);
        Result validation = await ValidateUpdateAsync(catalog, item, request, cancellationToken);
        if (validation.IsFailure) return Result.Failure<NavigationAdminItemResponse>(validation.Error);

        item.Key = request.Key.Trim(); item.Label = request.Label.Trim(); item.Href = NormalizeHref(request.Href);
        item.Icon = request.Icon.Trim(); item.Order = request.Order; item.ParentId = request.ParentId; item.IsActive = request.IsActive;
        await navigationCatalogRepository.UpdateItemAsync(item, cancellationToken);
        return ToResponse(catalog, item);
    }

    private async Task<Result> DeleteItemAsync(Guid actorUserId, Task<Result<NavigationCatalog>> catalogTask, Guid itemId, CancellationToken cancellationToken)
    {
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure) return guard;
        Result<NavigationCatalog> catalogResult = await catalogTask;
        if (catalogResult.IsFailure) return Result.Failure(catalogResult.Error);
        NavigationItem? item = await navigationCatalogRepository.GetItemAsync(catalogResult.Value.Id, itemId, cancellationToken);
        if (item is null) return Result.Failure(AuthErrors.Navigation.CatalogNotFound);
        if (item.Children.Count > 0) return Result.Failure(AuthErrors.Navigation.ParentHasChildren);
        await navigationCatalogRepository.DeleteItemAsync(item, cancellationToken);
        return Result.Success();
    }

    private async Task<Result> AddPermissionAsync(Guid actorUserId, Task<Result<NavigationCatalog>> catalogTask, Guid itemId, Guid permissionId, CancellationToken cancellationToken)
    {
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure) return guard;
        Result<NavigationCatalog> catalogResult = await catalogTask;
        if (catalogResult.IsFailure) return Result.Failure(catalogResult.Error);
        NavigationItem? item = await navigationCatalogRepository.GetItemAsync(catalogResult.Value.Id, itemId, cancellationToken);
        if (item is null || item.Href is null) return Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        if (item.Permissions.Any(binding => binding.PermissionId == permissionId)) return Result.Success();
        if (await navigationCatalogRepository.GetAssignablePermissionAsync(permissionId, cancellationToken) is null) return Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        await navigationCatalogRepository.AddPermissionAsync(new NavigationItemPermission { NavigationItemId = itemId, PermissionId = permissionId }, cancellationToken);
        return Result.Success();
    }

    private async Task<Result> RemovePermissionAsync(Guid actorUserId, Task<Result<NavigationCatalog>> catalogTask, Guid itemId, Guid permissionId, CancellationToken cancellationToken)
    {
        Result guard = await EnsureManageAsync(actorUserId, cancellationToken);
        if (guard.IsFailure) return guard;
        Result<NavigationCatalog> catalogResult = await catalogTask;
        if (catalogResult.IsFailure) return Result.Failure(catalogResult.Error);
        NavigationItem? item = await navigationCatalogRepository.GetItemAsync(catalogResult.Value.Id, itemId, cancellationToken);
        if (item is null || item.Href is null || !item.Permissions.Any(binding => binding.PermissionId == permissionId)) return Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        if (item.Permissions.Count == 1) return Result.Failure(AuthErrors.Navigation.LastPermission);
        await navigationCatalogRepository.RemovePermissionAsync(itemId, permissionId, cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ValidateCreateAsync(NavigationCatalog catalog, CreateNavigationItemRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidFields(request.Key, request.Label, request.Href, request.Icon) || (request.ParentId is null && request.Href is null && (request.PermissionIds?.Count ?? 0) != 0)) return Result.Failure(AuthErrors.Navigation.InvalidItem);
        if (await navigationCatalogRepository.KeyExistsAsync(catalog.Id, request.Key.Trim(), cancellationToken: cancellationToken)) return Result.Failure(AuthErrors.Navigation.DuplicateKey);
        if (!await HasValidParentAsync(catalog, null, request.ParentId, cancellationToken)) return Result.Failure(AuthErrors.Navigation.InvalidItem);
        return await ValidatePermissionsAsync(request.Href, request.PermissionIds, cancellationToken);
    }

    private async Task<Result> ValidateUpdateAsync(NavigationCatalog catalog, NavigationItem item, UpdateNavigationItemRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidFields(request.Key, request.Label, request.Href, request.Icon)) return Result.Failure(AuthErrors.Navigation.InvalidItem);
        if (await navigationCatalogRepository.KeyExistsAsync(catalog.Id, request.Key.Trim(), item.Id, cancellationToken)) return Result.Failure(AuthErrors.Navigation.DuplicateKey);
        if (!await HasValidParentAsync(catalog, item.Id, request.ParentId, cancellationToken)) return Result.Failure(AuthErrors.Navigation.InvalidItem);
        if (request.Href is null && item.Children.Count == 0) return Result.Failure(AuthErrors.Navigation.InvalidItem);
        if (request.Href is null && item.Permissions.Count > 0) return Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        if (request.Href is not null && item.Permissions.Count == 0) return Result.Failure(AuthErrors.Navigation.LastPermission);
        return Result.Success();
    }

    private async Task<Result> ValidatePermissionsAsync(string? href, IReadOnlyCollection<Guid>? permissionIds, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> ids = permissionIds ?? [];
        if (href is null) return ids.Count == 0 ? Result.Success() : Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) return Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        foreach (Guid permissionId in ids)
        {
            if (await navigationCatalogRepository.GetAssignablePermissionAsync(permissionId, cancellationToken) is null) return Result.Failure(AuthErrors.Navigation.InvalidPermissionBinding);
        }
        return Result.Success();
    }

    private async Task<bool> HasValidParentAsync(NavigationCatalog catalog, Guid? itemId, Guid? parentId, CancellationToken cancellationToken)
    {
        if (parentId is null) return true;
        if (parentId == itemId) return false;
        NavigationItem? parent = await navigationCatalogRepository.GetItemAsync(catalog.Id, parentId.Value, cancellationToken);
        if (parent is null || parent.Href is not null) return false;
        Guid? cursor = parent.ParentId;
        while (cursor is Guid current)
        {
            if (current == itemId) return false;
            cursor = catalog.Items.SingleOrDefault(candidate => candidate.Id == current)?.ParentId;
        }
        return true;
    }

    private async Task<Result<NavigationCatalog>> GetTemplateCatalogAsync(OrganizationType dashboardType, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(dashboardType)) return Result.Failure<NavigationCatalog>(AuthErrors.Navigation.InvalidDashboardType);
        NavigationCatalog? catalog = await navigationCatalogRepository.GetTemplateAsync(dashboardType, cancellationToken);
        return catalog is null ? Result.Failure<NavigationCatalog>(AuthErrors.Navigation.CatalogNotFound) : catalog;
    }

    private async Task<Result<NavigationCatalog>> GetOrganizationCatalogAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty) return Result.Failure<NavigationCatalog>(AuthErrors.Navigation.InvalidOrganizationContext);
        var organization = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        NavigationCatalog? catalog = await navigationCatalogRepository.GetOrganizationCatalogAsync(organizationId, cancellationToken);
        return organization is null || catalog is null || organization.Type != catalog.DashboardType
            ? Result.Failure<NavigationCatalog>(AuthErrors.Navigation.CatalogNotFound)
            : catalog;
    }

    private async Task<Result> EnsureManageAsync(Guid actorUserId, CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty) return Result.Failure(AuthErrors.Navigation.ManageDenied);
        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive) return Result.Failure(AuthErrors.Navigation.ManageDenied);
        IReadOnlyList<string> permissions = await roleRepository.GetUserPermissionCodesAsync(actorUserId, cancellationToken);
        return permissions.Contains(PermissionCode.IdentityNavigationManage, StringComparer.Ordinal)
            ? Result.Success()
            : Result.Failure(AuthErrors.Navigation.ManageDenied);
    }

    private static NavigationAdminItemResponse ToResponse(NavigationCatalog catalog, NavigationItem item) => new(
        item.Id, catalog.DashboardType.ToString(), catalog.OrganizationId, item.ParentId, item.Key, item.Label, item.Href,
        item.Icon, item.Order, item.IsActive,
        item.Permissions.OrderBy(binding => binding.Permission.Code).Select(binding => new NavigationPermissionResponse(binding.PermissionId, binding.Permission.Code, binding.Permission.Description)).ToArray());

    private static bool IsValidFields(string key, string label, string? href, string icon) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 120 && Regex.IsMatch(key, "^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.CultureInvariant) &&
        !string.IsNullOrWhiteSpace(label) && label.Trim().Length <= 150 && !string.IsNullOrWhiteSpace(icon) && icon.Trim().Length <= 100 && IsValidHref(href);

    private static bool IsValidHref(string? href) => href is null || (href.Length <= 500 && href.Length > 0 && href[0] == '/' && !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains("://", StringComparison.Ordinal) && !href.Contains('\r') && !href.Contains('\n'));
    private static string? NormalizeHref(string? href) => string.IsNullOrWhiteSpace(href) ? null : href.Trim();
}
