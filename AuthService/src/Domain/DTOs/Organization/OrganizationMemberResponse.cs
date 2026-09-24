using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.DTOs.Organization;

public sealed record OrganizationMemberResponse(
    Guid Id,
    Guid UserId,
    string Email,
    string FullName,
    Guid RoleId,
    string RoleName,
    MembershipStatus Status,
    DateTime JoinedAtUtc);
