namespace Dprental.CatalogService.Application.IServices;

/// <summary>
/// Danh tính và quyền của caller trong request hiện tại, chỉ lấy từ claims của access token đã verify chữ ký.
/// </summary>
public interface ICatalogCallerContext
{
    bool IsAuthenticated { get; }

    /// <summary>Token của service principal (<c>u = svc</c>), không đại diện cho người dùng.</summary>
    bool IsService { get; }

    /// <summary>Người dùng có role Admin; service token không bao giờ được coi là Admin.</summary>
    bool IsAdmin { get; }

    /// <summary>Giá trị claim <c>sub</c>; null khi anonymous.</summary>
    Guid? UserId { get; }

    IReadOnlySet<string> Roles { get; }

    IReadOnlySet<string> Permissions { get; }

    /// <summary>Các organization mà AuthService xác nhận caller thuộc về (claim <c>o</c>).</summary>
    IReadOnlySet<Guid> OrganizationIds { get; }
}
