using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

/// <summary>Thông tin organization. <see cref="IsActive"/> giữ lại cho client cũ, tương đương <c>Status == Active</c>.</summary>
public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    OrganizationType Type,
    bool IsActive,
    DateTime CreatedAtUtc,
    OrganizationStatus Status,
    string? TaxCode = null,
    string? BusinessLicenseNumber = null,
    DateOnly? BusinessLicenseIssuedDate = null,
    string? BusinessLicenseIssuedPlace = null,
    DateOnly? EstablishedDate = null,
    string? LegalRepresentativeName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Address = null,
    string? Province = null,
    string? Ward = null,
    string? Website = null,
    string? LogoUrl = null,
    string? Description = null,
    string? BusinessLicenseDocumentUrl = null,
    string? RejectionReason = null,
    DateTime? ReviewedAtUtc = null);
