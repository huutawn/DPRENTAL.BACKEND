namespace Dprental.AuthService.Domain.DTOs.Navigation;

/// <summary>Item đầy đủ cho màn quản trị catalog, gồm permission bindings không bị filter theo user gọi.</summary>
public sealed record NavigationAdminItemResponse(
    Guid Id,
    string DashboardType,
    Guid? OrganizationId,
    Guid? ParentId,
    string Key,
    string Label,
    string? Href,
    string Icon,
    int Order,
    bool IsActive,
    IReadOnlyCollection<NavigationPermissionResponse> Permissions);

public sealed record NavigationPermissionResponse(Guid Id, string Code, string Description);

public sealed record CreateNavigationItemRequest(
    string Key,
    string Label,
    string? Href,
    string Icon,
    int Order,
    Guid? ParentId,
    IReadOnlyCollection<Guid>? PermissionIds);

public sealed record UpdateNavigationItemRequest(
    string Key,
    string Label,
    string? Href,
    string Icon,
    int Order,
    Guid? ParentId,
    bool IsActive);

public sealed record NavigationPermissionBindingRequest(Guid PermissionId);
