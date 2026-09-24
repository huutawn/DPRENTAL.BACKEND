using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.DTOs.Organization;

public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    OrganizationType Type,
    bool IsActive,
    DateTime CreatedAtUtc);
