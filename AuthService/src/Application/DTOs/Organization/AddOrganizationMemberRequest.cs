using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Organization;

public sealed record AddOrganizationMemberRequest(
    [Required]
    Guid UserId,

    [Required]
    Guid RoleId);
