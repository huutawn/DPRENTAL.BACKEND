namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Contract danh tính người gọi cho các use case của RentalService.
/// Use case lấy danh tính và quyền qua context thay vì tự đọc HttpContext hoặc ClaimsPrincipal;
/// có thể truyền context kiểm thử mà không cần tạo request HTTP.
/// </summary>
public interface IRentalCallerContext
{
    /// <summary>
    /// Cho biết caller hiện có danh tính đã được xác thực hợp lệ hay chưa.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Phân biệt caller dùng token service với người dùng cá nhân.
    /// Theo contract Auth, token service có sub là UUID và u=svc; worker tự chạy không mặc nhiên có token này.
    /// </summary>
    bool IsService { get; }

    /// <summary>
    /// ID người dùng cá nhân lấy từ claim sub.
    /// Luôn null nếu chưa xác thực hoặc là token service (u=svc), tránh service giả danh renter/supplier.
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// Tên người dùng hoặc định danh service theo claims.
    /// </summary>
    string? Username { get; }

    /// <summary>
    /// Cờ hoàn tất hồ sơ từ claim pc; theo contract Auth phải là boolean true để đủ điều kiện checkout.
    /// Luôn false đối với token service.
    /// </summary>
    bool IsProfileCompleted { get; }

    /// <summary>
    /// Tập vai trò bất biến từ claim r, bao gồm các alias đã chuẩn hóa.
    /// </summary>
    IReadOnlySet<string> Roles { get; }

    /// <summary>
    /// Tập quyền bất biến từ claim p.
    /// </summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>
    /// Kiểm tra vai trò theo tên chính thức hoặc alias đã chuẩn hóa.
    /// </summary>
    bool HasRole(string role);

    /// <summary>
    /// Kiểm tra caller có quyền được chỉ định hay không.
    /// </summary>
    bool HasPermission(string permission);

    /// <summary>
    /// Kiểm tra quyền Admin hệ thống/nền tảng theo các tên admin, adm, admin_platform hoặc adp.
    /// </summary>
    bool IsAdmin();
}
