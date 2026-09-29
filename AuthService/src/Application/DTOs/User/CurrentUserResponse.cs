using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>Thông tin hồ sơ, quyền/vai trò và các organization đang active của người dùng hiện tại.</summary>
/// <param name="MaskedCitizenId">CCCD đã che, chỉ lộ 4 số cuối (vd <c>********1234</c>).</param>
/// <param name="HasPassword">Cho biết tài khoản đã thiết lập mật khẩu hay chưa (để hiển thị nút đổi mật khẩu / gỡ liên kết Google).</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string FullName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<UserOrganizationResponse> Organizations,
    DateTime CreatedAt,
    bool IsProfileCompleted,
    Guid? AvatarMediaId = null,
    string? AvatarUrl = null,
    string? PhoneNumber = null,
    DateOnly? DateOfBirth = null,
    string? MaskedCitizenId = null,
    bool HasPassword = false);

/// <summary>Một organization mà user đang là thành viên Active, kèm role trong organization đó.</summary>
public sealed record UserOrganizationResponse(
    Guid OrganizationId,
    string Name,
    string Slug,
    OrganizationType Type,
    string RoleName);
