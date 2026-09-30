namespace Dprental.Gateway.Security;

/// <summary>
/// Định nghĩa các hằng số tiêu đề HTTP (headers) được sử dụng xuyên suốt Gateway,
/// bao gồm các header danh tính đáng tin cậy, header truy vết và các header không tin cậy từ phía client.
/// </summary>
public static class GatewayHeaderConstants
{
    /// <summary>
    /// Tiêu đề HTTP định danh người dùng duy nhất được chuyển tiếp tới downstream service.
    /// </summary>
    public const string UserId = "X-User-Id";

    /// <summary>
    /// Tiêu đề HTTP định danh đối tác hoặc bên liên quan (Party) của người dùng.
    /// </summary>
    public const string PartyId = "X-Party-Id";

    /// <summary>
    /// Tiêu đề HTTP biểu diễn vai trò của người dùng đã được xác thực qua JWT.
    /// </summary>
    public const string Role = "X-Role";

    /// <summary>
    /// Tiêu đề HTTP định danh tên của service gọi nội bộ hoặc Gateway.
    /// </summary>
    public const string ServiceName = "X-Service-Name";

    /// <summary>
    /// Tiêu đề HTTP dùng để tương quan các log và trace trên toàn hệ thống phân tán.
    /// </summary>
    public const string CorrelationId = "X-Correlation-ID";

    /// <summary>
    /// Tiêu đề HTTP lưu địa chỉ IP gốc của client khi đi qua proxy hoặc load balancer.
    /// </summary>
    public const string ForwardedFor = "X-Forwarded-For";

    /// <summary>
    /// Tiêu đề HTTP lưu giao thức gốc (HTTP/HTTPS) của client khi đi qua proxy.
    /// </summary>
    public const string ForwardedProto = "X-Forwarded-Proto";

    /// <summary>
    /// Tiêu đề HTTP lưu host gốc mà client gửi tới trước khi qua reverse proxy.
    /// </summary>
    public const string ForwardedHost = "X-Forwarded-Host";

    /// <summary>
    /// Danh sách các tiêu đề nội bộ nhạy cảm mà client không bao giờ được phép giả mạo.
    /// Gateway bắt buộc phải loại bỏ hoặc ghi đè các tiêu đề này trước khi chuyển tiếp tới downstream.
    /// </summary>
    public static readonly IReadOnlyCollection<string> UntrustedClientHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        UserId,
        PartyId,
        Role,
        "X-Roles",
        ServiceName,
        "X-Permissions",
        "X-Permission",
        "X-Scope",
        "X-Scopes"
    };
}
