namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>
/// Yêu cầu gán hoặc thu hồi một role trên một user. <paramref name="OrganizationId"/> chỉ cần khi
/// role mang <see cref="Dprental.AuthService.Common.Enums.RoleScope.Organization"/> — role toàn cục để trống.
/// </summary>
public sealed record AssignRoleRequest(Guid TargetUserId, Guid RoleId, Guid? OrganizationId = null);
