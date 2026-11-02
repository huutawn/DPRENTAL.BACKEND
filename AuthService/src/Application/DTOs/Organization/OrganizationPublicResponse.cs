using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.DTOs.Organization;

/// <summary>Thông tin tổ chức được phép chia sẻ với người dùng khác.</summary>
public sealed record OrganizationPublicResponse(Guid Id, string Name, string Slug, OrganizationType Type,
    OrganizationStatus Status, string? ContactPhone, Guid? LogoMediaId, string? LogoUrl);
