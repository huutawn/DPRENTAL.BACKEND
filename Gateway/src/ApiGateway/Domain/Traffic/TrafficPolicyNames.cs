namespace Dprental.Gateway.Domain.Traffic;

/// <summary>
/// Định nghĩa các tên định danh chính sách điều phối lưu lượng (Traffic & Rate Limiting Policies)
/// áp dụng trên API Gateway.
/// </summary>
public static class TrafficPolicyNames
{
    /// <summary>
    /// Chính sách giới hạn tần suất cho người dùng ẩn danh (chưa đăng nhập), phân vùng theo địa chỉ IP của client.
    /// </summary>
    public const string Anonymous = "anonymous";

    /// <summary>
    /// Chính sách giới hạn tần suất cho người dùng đã xác thực, phân vùng theo UserId lấy từ token JWT.
    /// </summary>
    public const string Authenticated = "authenticated";

    /// <summary>
    /// Chính sách giới hạn tần suất nghiêm ngặt cho các endpoint nhạy cảm (như đăng nhập, xác thực, thanh toán).
    /// </summary>
    public const string Sensitive = "sensitive";

    /// <summary>
    /// Tên chính sách phân vùng tổng thể (Partitioned Rate Limiter Policy) của API Gateway.
    /// </summary>
    public const string GlobalPartitioned = "gateway-global-rate-limiter";
}
