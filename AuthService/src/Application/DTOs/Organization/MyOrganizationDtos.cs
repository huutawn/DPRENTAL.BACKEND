using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

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

/// <summary>Thông tin lời mời tham gia tổ chức đang chờ người dùng xử lý.</summary>
public sealed record OrganizationInvitationResponse(
    Guid MembershipId,
    Guid OrganizationId,
    string Name,
    string Slug,
    OrganizationType Type,
    string RoleName,
    DateTime InvitedAtUtc);
