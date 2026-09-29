using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

/// <summary>
/// Chi tiết organization (GET /{id}, duyệt/từ chối, sửa hồ sơ). Trạng thái chỉ đọc qua <see cref="Status"/>.
/// CCCD người đại diện không bao giờ được trả ra.
/// </summary>
public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    OrganizationType Type,
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
    Guid? LogoMediaId = null,
    string? LogoUrl = null,
    string? Description = null,
    Guid? BusinessLicenseDocumentMediaId = null,
    string? BusinessLicenseDocumentUrl = null,
    DateTime? BusinessLicenseDocumentUrlExpiresAtUtc = null,
    string? RejectionReason = null,
    DateTime? ReviewedAtUtc = null);

/// <summary>
/// Một dòng trong danh sách organization. Không có thông tin pháp lý/liên hệ — xem chi tiết qua GET /{id}.
/// </summary>
public sealed record OrganizationSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    OrganizationType Type,
    OrganizationStatus Status,
    Guid? LogoMediaId,
    string? LogoUrl,
    string? Province,
    DateTime CreatedAtUtc);

public sealed record OrganizationRegisteredResponse(
    Guid AccountId,
    Guid OrganizationId,
    string Slug,
    OrganizationStatus Status);

/// <summary>Thông tin chi tiết một tổ chức mà người dùng hiện tại đang là thành viên.</summary>
public sealed record MyOrganizationResponse(
    Guid MembershipId,
    Guid OrganizationId,
    string Name,
    string Slug,
    OrganizationType Type,
    OrganizationStatus OrganizationStatus,
    string RoleName,
    MembershipStatus MembershipStatus,
    DateTime JoinedAtUtc);
