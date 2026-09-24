using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Domain.DTOs.Organization;

public sealed record UpdateOrganizationRequest(
    [MaxLength(150)]
    string? Name = null,

    bool? IsActive = null);
