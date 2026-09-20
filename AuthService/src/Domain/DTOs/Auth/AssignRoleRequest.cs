namespace Dprental.AuthService.Domain.DTOs.Auth;

/// <summary>Yêu cầu gán hoặc thu hồi một role trên một user.</summary>
public sealed record AssignRoleRequest(Guid TargetUserId, Guid RoleId);
