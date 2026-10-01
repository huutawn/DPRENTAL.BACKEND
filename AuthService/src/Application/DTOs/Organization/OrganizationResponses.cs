using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

/// <summary>
/// Chi tiết organization (GET /{id}, duyệt/từ chối, sửa hồ sơ). Trạng thái chỉ đọc qua <see cref="Status"/>.
/// CCCD người đại diện không bao giờ được trả ra.
/// </summary>
/// <param name="SubmittedAtUtc">Lần nộp gần nhất (lúc đăng ký, hoặc lúc gửi lại sau khi bị từ chối).</param>
/// <param name="ReviewDueAtUtc">Hạn Platform duyệt; chỉ có khi đang Pending.</param>
/// <param name="CanRemindAtUtc">Thời điểm sớm nhất Owner được nhắc duyệt; chỉ có khi đang Pending.</param>
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
    DateTime? ReviewedAtUtc = null,
    DateTime? SubmittedAtUtc = null,
    DateTime? ReviewDueAtUtc = null,
    DateTime? CanRemindAtUtc = null,
    DateTime? LastRemindedAtUtc = null);

/// <summary>
/// Một dòng trong danh sách organization. Không có thông tin pháp lý/liên hệ — xem chi tiết qua GET /{id}.
/// </summary>
/// <param name="IsOverdue">Đang Pending và đã quá hạn duyệt.</param>
public sealed record OrganizationSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    OrganizationType Type,
    OrganizationStatus Status,
    Guid? LogoMediaId,
    string? LogoUrl,
    string? Province,
    DateTime CreatedAtUtc,
    DateTime? SubmittedAtUtc = null,
    DateTime? LastRemindedAtUtc = null,
    bool IsOverdue = false);

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
