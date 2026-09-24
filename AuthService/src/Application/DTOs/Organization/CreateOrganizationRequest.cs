using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

public sealed record CreateOrganizationRequest(
    [Required]
    [MaxLength(150)]
    string Name,

    [Required]
    [MaxLength(100)]
    string Slug,

    OrganizationType Type);
