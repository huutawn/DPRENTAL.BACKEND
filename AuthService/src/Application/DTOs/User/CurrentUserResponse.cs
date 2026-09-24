using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>Thông tin hồ sơ, quyền/vai trò và các organization đang active của người dùng hiện tại.</summary>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string FullName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<UserOrganizationResponse> Organizations,
    DateTime CreatedAt);

/// <summary>Một organization mà user đang là thành viên Active, kèm role trong organization đó.</summary>
public sealed record UserOrganizationResponse(
    Guid OrganizationId,
    string Name,
    string Slug,
    OrganizationType Type,
    string RoleName);
