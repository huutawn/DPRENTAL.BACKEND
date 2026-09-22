using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Navigation;
using Dprental.AuthService.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>CRUD template và catalog dashboard; chỉ global admin có identity.navigation.manage được thao tác.</summary>
[ApiController]
[Produces("application/json")]
[Authorize(Policy = Permissions.Navigation.Manage)]
public sealed class NavigationAdminController(INavigationAdminService navigationAdminService) : ControllerBase
{
    /// <summary>Liệt kê permission identity.* có thể gắn vào menu.</summary>
    [HttpGet("navigation/permissions")]
    public async Task<ActionResult<IReadOnlyCollection<NavigationPermissionResponse>>> GetPermissions(CancellationToken cancellationToken) =>
        await ExecuteAsync(actor => navigationAdminService.GetAssignablePermissionsAsync(actor, cancellationToken));

    /// <summary>Đọc toàn bộ item của template dashboard.</summary>
    [HttpGet("navigation/templates/{dashboardType}/items")]
    public async Task<ActionResult<IReadOnlyCollection<NavigationAdminItemResponse>>> GetTemplate(string dashboardType, CancellationToken cancellationToken) =>
        await ExecuteDashboardAsync(dashboardType, type => navigationAdminService.GetTemplateItemsAsync(CurrentUserId, type, cancellationToken));

    /// <summary>Tạo item trong template dashboard.</summary>
    [HttpPost("navigation/templates/{dashboardType}/items")]
    public async Task<ActionResult<NavigationAdminItemResponse>> CreateTemplate(string dashboardType, CreateNavigationItemRequest request, CancellationToken cancellationToken) =>
        await ExecuteDashboardAsync(dashboardType, type => navigationAdminService.CreateTemplateItemAsync(CurrentUserId, type, request, cancellationToken), StatusCodes.Status201Created);

    /// <summary>Cập nhật item template.</summary>
    [HttpPut("navigation/templates/{dashboardType}/items/{itemId:guid}")]
    public async Task<ActionResult<NavigationAdminItemResponse>> UpdateTemplate(string dashboardType, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken) =>
        await ExecuteDashboardAsync(dashboardType, type => navigationAdminService.UpdateTemplateItemAsync(CurrentUserId, type, itemId, request, cancellationToken));

    /// <summary>Xóa item template không còn child.</summary>
    [HttpDelete("navigation/templates/{dashboardType}/items/{itemId:guid}")]
    public async Task<IActionResult> DeleteTemplate(string dashboardType, Guid itemId, CancellationToken cancellationToken) =>
        await ExecuteDashboardVoidAsync(dashboardType, type => navigationAdminService.DeleteTemplateItemAsync(CurrentUserId, type, itemId, cancellationToken));

    /// <summary>Gắn permission vào item template.</summary>
    [HttpPost("navigation/templates/{dashboardType}/items/{itemId:guid}/permissions")]
    public async Task<IActionResult> AddTemplatePermission(string dashboardType, Guid itemId, NavigationPermissionBindingRequest request, CancellationToken cancellationToken) =>
        await ExecuteDashboardVoidAsync(dashboardType, type => navigationAdminService.AddTemplatePermissionAsync(CurrentUserId, type, itemId, request.PermissionId, cancellationToken));

    /// <summary>Gỡ permission khỏi item template.</summary>
    [HttpDelete("navigation/templates/{dashboardType}/items/{itemId:guid}/permissions/{permissionId:guid}")]
    public async Task<IActionResult> RemoveTemplatePermission(string dashboardType, Guid itemId, Guid permissionId, CancellationToken cancellationToken) =>
        await ExecuteDashboardVoidAsync(dashboardType, type => navigationAdminService.RemoveTemplatePermissionAsync(CurrentUserId, type, itemId, permissionId, cancellationToken));

    /// <summary>Đọc catalog đã clone của một organization.</summary>
    [HttpGet("organizations/{organizationId:guid}/navigation/items")]
    public async Task<ActionResult<IReadOnlyCollection<NavigationAdminItemResponse>>> GetOrganization(Guid organizationId, CancellationToken cancellationToken) =>
        await ExecuteAsync(actor => navigationAdminService.GetOrganizationItemsAsync(actor, organizationId, cancellationToken));

    /// <summary>Tạo item trong catalog organization.</summary>
    [HttpPost("organizations/{organizationId:guid}/navigation/items")]
    public async Task<ActionResult<NavigationAdminItemResponse>> CreateOrganization(Guid organizationId, CreateNavigationItemRequest request, CancellationToken cancellationToken) =>
        await ExecuteAsync(actor => navigationAdminService.CreateOrganizationItemAsync(actor, organizationId, request, cancellationToken), StatusCodes.Status201Created);

    /// <summary>Cập nhật item catalog organization.</summary>
    [HttpPut("organizations/{organizationId:guid}/navigation/items/{itemId:guid}")]
    public async Task<ActionResult<NavigationAdminItemResponse>> UpdateOrganization(Guid organizationId, Guid itemId, UpdateNavigationItemRequest request, CancellationToken cancellationToken) =>
        await ExecuteAsync(actor => navigationAdminService.UpdateOrganizationItemAsync(actor, organizationId, itemId, request, cancellationToken));

    /// <summary>Xóa item organization không còn child.</summary>
    [HttpDelete("organizations/{organizationId:guid}/navigation/items/{itemId:guid}")]
    public async Task<IActionResult> DeleteOrganization(Guid organizationId, Guid itemId, CancellationToken cancellationToken) =>
        await ExecuteVoidAsync(actor => navigationAdminService.DeleteOrganizationItemAsync(actor, organizationId, itemId, cancellationToken));

    /// <summary>Gắn permission vào item organization.</summary>
    [HttpPost("organizations/{organizationId:guid}/navigation/items/{itemId:guid}/permissions")]
    public async Task<IActionResult> AddOrganizationPermission(Guid organizationId, Guid itemId, NavigationPermissionBindingRequest request, CancellationToken cancellationToken) =>
        await ExecuteVoidAsync(actor => navigationAdminService.AddOrganizationPermissionAsync(actor, organizationId, itemId, request.PermissionId, cancellationToken));

    /// <summary>Gỡ permission khỏi item organization.</summary>
    [HttpDelete("organizations/{organizationId:guid}/navigation/items/{itemId:guid}/permissions/{permissionId:guid}")]
    public async Task<IActionResult> RemoveOrganizationPermission(Guid organizationId, Guid itemId, Guid permissionId, CancellationToken cancellationToken) =>
        await ExecuteVoidAsync(actor => navigationAdminService.RemoveOrganizationPermissionAsync(actor, organizationId, itemId, permissionId, cancellationToken));

    private Guid CurrentUserId => User.GetJwtSub() ?? Guid.Empty;

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Guid, Task<Result<T>>> operation, int successStatus = StatusCodes.Status200OK)
    {
        if (CurrentUserId == Guid.Empty) return Unauthorized();
        Result<T> result = await operation(CurrentUserId);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(successStatus, result.Value);
    }

    private async Task<IActionResult> ExecuteVoidAsync(Func<Guid, Task<Result>> operation)
    {
        if (CurrentUserId == Guid.Empty) return Unauthorized();
        Result result = await operation(CurrentUserId);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    private async Task<ActionResult<T>> ExecuteDashboardAsync<T>(string dashboardType, Func<OrganizationType, Task<Result<T>>> operation, int successStatus = StatusCodes.Status200OK)
    {
        if (!TryParseDashboardType(dashboardType, out OrganizationType type)) return this.ToProblem(AuthErrors.Navigation.InvalidDashboardType);
        if (CurrentUserId == Guid.Empty) return Unauthorized();
        Result<T> result = await operation(type);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(successStatus, result.Value);
    }

    private async Task<IActionResult> ExecuteDashboardVoidAsync(string dashboardType, Func<OrganizationType, Task<Result>> operation)
    {
        if (!TryParseDashboardType(dashboardType, out OrganizationType type)) return this.ToProblem(AuthErrors.Navigation.InvalidDashboardType);
        if (CurrentUserId == Guid.Empty) return Unauthorized();
        Result result = await operation(type);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    private static bool TryParseDashboardType(string value, out OrganizationType dashboardType) =>
        Enum.TryParse(value, ignoreCase: true, out dashboardType) && Enum.IsDefined(dashboardType);
}
