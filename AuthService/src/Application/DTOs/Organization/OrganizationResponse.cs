using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    OrganizationType Type,
    bool IsActive,
    DateTime CreatedAtUtc);
