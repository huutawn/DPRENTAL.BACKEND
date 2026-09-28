using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>
/// Yêu cầu gán hoặc thu hồi một role toàn cục trên một user. MVP không có role theo Organization:
/// Owner được gán khi đăng ký tổ chức, không đổi qua API này.
/// </summary>
public sealed record AssignRoleRequest(
    [Required] Guid TargetUserId,
    [Required] Guid RoleId);
