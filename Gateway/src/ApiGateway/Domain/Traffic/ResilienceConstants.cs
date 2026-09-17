namespace Dprental.Gateway.Domain.Traffic;

/// <summary>
/// Định nghĩa các hằng số quy chuẩn về khả năng phục hồi (Resilience), thời gian chờ (Timeout)
/// và các ràng buộc an toàn khi thực thi lại request (Retry Safety).
/// </summary>
public static class ResilienceConstants
{
    /// <summary>
    /// Thời gian chờ mặc định cho các request tới downstream microservices (tính bằng giây).
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    /// <summary>
    /// Danh sách các phương thức HTTP không an toàn (Non-Idempotent / Unsafe Methods)
    /// có khả năng làm thay đổi trạng thái nghiệp vụ.
    /// Gateway tuyệt đối KHÔNG tự ý retry các phương thức này khi gặp lỗi timeout hoặc connection failure
    /// nhằm ngăn chặn tác dụng phụ trùng lặp (Duplicate Business Effects).
    /// </summary>
    public static readonly IReadOnlyCollection<string> UnsafeHttpMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "POST",
        "PATCH",
        "CONNECT"
    };

    /// <summary>
    /// Tên tiêu đề HTTP chuẩn báo hiệu thời gian client nên đợi trước khi thử lại sau khi bị Rate Limit (429).
    /// </summary>
    public const string RetryAfterHeader = "Retry-After";
}
