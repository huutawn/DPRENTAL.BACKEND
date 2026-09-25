using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Mapping;

public static class OrganizationMappingExtensions
{
    /// <summary>CCCD người đại diện không được trả ra ngoài.</summary>
    public static OrganizationResponse ToResponse(this Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return new OrganizationResponse(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.Type,
            organization.Status == OrganizationStatus.Active,
            organization.CreatedAtUtc,
            organization.Status,
            organization.TaxCode,
            organization.BusinessLicenseNumber,
            organization.BusinessLicenseIssuedDate,
            organization.BusinessLicenseIssuedPlace,
            organization.EstablishedDate,
            organization.LegalRepresentativeName,
            organization.ContactEmail,
            organization.ContactPhone,
            organization.Address,
            organization.Province,
            organization.Ward,
            organization.Website,
            organization.LogoUrl,
            organization.Description,
            organization.BusinessLicenseDocumentUrl,
            organization.RejectionReason,
            organization.ReviewedAtUtc);
    }

    public static OrganizationMemberResponse ToResponse(this OrganizationMembership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return new OrganizationMemberResponse(
            membership.Id,
            membership.UserId,
            membership.User?.Email ?? string.Empty,
            membership.User?.FullName ?? string.Empty,
            membership.RoleId,
            membership.Role?.Name ?? string.Empty,
            membership.Status,
            membership.JoinedAtUtc);
    }
}
