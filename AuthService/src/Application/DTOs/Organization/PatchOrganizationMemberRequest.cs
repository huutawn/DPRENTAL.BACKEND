using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.DTOs.Organization;

public sealed record PatchOrganizationMemberRequest(
    Guid? RoleId = null,
    MembershipStatus? Status = null);
