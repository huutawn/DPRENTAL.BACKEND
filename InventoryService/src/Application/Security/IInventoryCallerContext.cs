namespace Dprental.InventoryService.Application.Security;

/// <summary>
/// Ngữ cảnh người gọi đã xác thực của Inventory Service trích xuất từ JWT chuẩn.
/// </summary>
public interface IInventoryCallerContext
{
    /// <summary>
    /// Người gọi đã được xác thực danh tính qua JWT hợp lệ hay chưa.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Người gọi là Service Principal (u=svc và aud=inventory-api).
    /// </summary>
    bool IsService { get; }

    /// <summary>
    /// Người gọi là Human (người dùng cá nhân).
    /// </summary>
    bool IsHuman => IsAuthenticated && !IsService;

    /// <summary>
    /// Người gọi là Human Admin có claim r=adm (cho phép bypass quyền xuyên user).
    /// </summary>
    bool IsAdmin { get; }

    /// <summary>
    /// Định danh duy nhất của người dùng (từ sub claim của JWT).
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// Tập hợp các mã quyền (permissions) từ các claim p.
    /// </summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>
    /// Tập hợp các scopes được cấp phép từ các claim s (chỉ áp dụng cho Service).
    /// </summary>
    IReadOnlySet<string> Scopes { get; }

    /// <summary>
    /// Tập hợp các vai trò từ các claim r.
    /// </summary>
    IReadOnlySet<string> Roles { get; }

    /// <summary>
    /// Định danh tổ chức liên kết từ claim o (nếu có).
    /// </summary>
    Guid? OrganizationId { get; }
}
