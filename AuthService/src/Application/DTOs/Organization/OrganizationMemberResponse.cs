using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

public sealed record OrganizationMemberResponse(
    Guid Id,
    Guid UserId,
    string Email,
    string FullName,
    Guid RoleId,
    string RoleName,
    MembershipStatus Status,
    DateTime JoinedAtUtc);
