using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

public sealed record PatchOrganizationMemberRequest(
    Guid? RoleId = null,
    MembershipStatus? Status = null);
