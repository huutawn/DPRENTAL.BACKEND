using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.DTOs.Organization;

public sealed record CreateOrganizationRequest(
    [Required]
    [MaxLength(150)]
    string Name,

    [Required]
    [MaxLength(100)]
    string Slug,

    OrganizationType Type);
