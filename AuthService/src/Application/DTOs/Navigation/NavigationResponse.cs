namespace Dprental.AuthService.Domain.DTOs.Navigation;

/// <summary>Menu navigation được lọc theo quyền hiệu lực của user trong một organization context tùy chọn.</summary>
public sealed record NavigationResponse(
    Guid OrganizationId,
    string DashboardType,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<NavigationItemResponse> Items);

/// <summary>Một node của navigation tree. Parent container có <see cref="Href"/> bằng <c>null</c>.</summary>
public sealed record NavigationItemResponse(
    string Key,
    string Label,
    string? Href,
    string Icon,
    int Order,
    IReadOnlyCollection<NavigationItemResponse> Children);
