using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Mapping;

public static class OrganizationMappingExtensions
{
    /// <summary>CCCD người đại diện không được trả ra ngoài.</summary>
    public static OrganizationResponse ToResponse(
        this Organization organization,
        string? logoUrl = null,
        string? businessLicenseDocumentUrl = null,
        DateTime? businessLicenseDocumentUrlExpiresAtUtc = null,
        OrganizationReviewOptions? reviewOptions = null)
    {
        ArgumentNullException.ThrowIfNull(organization);
        reviewOptions ??= new OrganizationReviewOptions();
        return new OrganizationResponse(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.Type,
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
            organization.LogoMediaId,
            logoUrl,
            organization.Description,
            organization.BusinessLicenseDocumentMediaId,
            businessLicenseDocumentUrl,
            businessLicenseDocumentUrlExpiresAtUtc,
            organization.RejectionReason,
            organization.ReviewedAtUtc,
            OrganizationReviewSchedule.SubmittedAtUtc(organization),
            OrganizationReviewSchedule.ReviewDueAtUtc(organization, reviewOptions),
            OrganizationReviewSchedule.CanRemindAtUtc(organization, reviewOptions),
            organization.LastRemindedAtUtc);
    }

    public static OrganizationSummaryResponse ToSummaryResponse(
        this Organization organization,
        string? logoUrl = null,
        OrganizationReviewOptions? reviewOptions = null,
        DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(organization);
        reviewOptions ??= new OrganizationReviewOptions();
        return new OrganizationSummaryResponse(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.Type,
            organization.Status,
            organization.LogoMediaId,
            logoUrl,
            organization.Province,
            organization.CreatedAtUtc,
            OrganizationReviewSchedule.SubmittedAtUtc(organization),
            organization.LastRemindedAtUtc,
            utcNow is DateTime now && OrganizationReviewSchedule.IsOverdue(organization, reviewOptions, now));
    }
}
