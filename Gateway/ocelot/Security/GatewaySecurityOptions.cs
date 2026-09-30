namespace Dprental.Gateway.Security;

/// <summary>
/// Cấu hình tổng thể về bảo mật và bảo vệ request cho API Gateway.
/// Bao gồm CORS, Forwarded Headers, Request Size Protection, Security Headers và Identity Forwarding.
/// </summary>
public sealed class GatewaySecurityOptions
{
    /// <summary>
    /// Tên section trong file cấu hình appsettings.json.
    /// </summary>
    public const string SectionName = "Gateway:Security";

    /// <summary>
    /// Cấu hình chính sách chia sẻ tài nguyên giữa các nguồn gốc (CORS).
    /// </summary>
    public GatewayCorsOptions Cors { get; set; } = new();

    /// <summary>
    /// Cấu hình xử lý các tiêu đề được ủy thác (Forwarded Headers) từ proxy/ingress cấp trên.
    /// </summary>
    public GatewayForwardedHeadersOptions ForwardedHeaders { get; set; } = new();

    /// <summary>
    /// Cấu hình giới hạn kích thước thân request để chống tấn công từ chối dịch vụ (DoS/OOM).
    /// </summary>
    public GatewayRequestSizeOptions RequestSize { get; set; } = new();

    /// <summary>
    /// Cấu hình các tiêu đề bảo mật HTTP được gắn vào phản hồi trả về cho client.
    /// </summary>
    public GatewaySecurityHeadersOptions Headers { get; set; } = new();

    /// <summary>
    /// Cấu hình chuyển tiếp danh tính đáng tin cậy xuống các dịch vụ nội bộ (Downstream Services).
    /// </summary>
    public GatewayIdentityOptions Identity { get; set; } = new();
}

/// <summary>
/// Cấu hình chi tiết cho chính sách CORS của Gateway.
/// </summary>
public sealed class GatewayCorsOptions
{
    /// <summary>
    /// Danh sách các nguồn gốc (Origins) được phép gửi request tới Gateway (Whitelist).
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Danh sách các phương thức HTTP (Methods) được phép thực thi.
    /// </summary>
    public string[] AllowedMethods { get; set; } = ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD"];

    /// <summary>
    /// Danh sách các tiêu đề HTTP (Headers) mà client được phép gửi lên.
    /// </summary>
    public string[] AllowedHeaders { get; set; } = ["Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-ID", "Accept", "Origin", "X-Requested-With"];

    /// <summary>
    /// Danh sách các tiêu đề HTTP mà trình duyệt của client được phép đọc từ phản hồi.
    /// </summary>
    public string[] ExposedHeaders { get; set; } = ["X-Correlation-ID"];

    /// <summary>
    /// Cho phép gửi kèm thông tin xác thực (cookies, authorization headers) trong cross-origin request.
    /// </summary>
    public bool AllowCredentials { get; set; } = true;

    /// <summary>
    /// Thời gian tối đa (tính bằng giây) trình duyệt được phép lưu cache kết quả kiểm tra tiền trạm (Preflight OPTIONS).
    /// Mặc định: 3600 giây (1 giờ).
    /// </summary>
    public int PreflightMaxAgeSeconds { get; set; } = 3600;
}

/// <summary>
/// Cấu hình xử lý Forwarded Headers (X-Forwarded-For, X-Forwarded-Proto, X-Forwarded-Host).
/// </summary>
public sealed class GatewayForwardedHeadersOptions
{
    /// <summary>
    /// Bật hoặc tắt tính năng xử lý Forwarded Headers từ reverse proxy hoặc load balancer phía trước.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Giới hạn số lượng proxy trung gian được tin cậy trong chuỗi header. Mặc định là 1.
    /// </summary>
    public int? ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Danh sách dải mạng CIDR đáng tin cậy (ví dụ: "10.0.0.0/8", "192.168.1.0/24").
    /// </summary>
    public string[] KnownNetworks { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Danh sách địa chỉ IP proxy cụ thể được tin cậy (ví dụ: "127.0.0.1", "::1").
    /// </summary>
    public string[] KnownProxies { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Cấu hình giới hạn kích thước thân request (Request Size Protection).
/// </summary>
public sealed class GatewayRequestSizeOptions
{
    /// <summary>
    /// Kích thước tối đa cho phép của request body tính bằng byte.
    /// Mặc định: 10 MB (10,485,760 bytes).
    /// </summary>
    public long MaxRequestBodySizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Multipart upload limit for POST /api/v1/media/uploads.</summary>
    public long MediaUploadMaxRequestBodySizeBytes { get; set; } = 25 * 1024 * 1024;
}

/// <summary>
/// Cấu hình các tiêu đề bảo mật HTTP (Security Headers) áp dụng trên phản hồi từ Gateway.
/// </summary>
public sealed class GatewaySecurityHeadersOptions
{
    /// <summary>
    /// Bật tiêu đề X-Content-Type-Options: nosniff để ngăn chặn trình duyệt suy đoán sai MIME type.
    /// </summary>
    public bool EnableContentTypeOptions { get; set; } = true;

    /// <summary>
    /// Bật tiêu đề X-Frame-Options để chống tấn công Clickjacking.
    /// </summary>
    public bool EnableFrameOptions { get; set; } = true;

    /// <summary>
    /// Giá trị cho tiêu đề X-Frame-Options (DENY hoặc SAMEORIGIN). Mặc định: DENY.
    /// </summary>
    public string FrameOptions { get; set; } = "DENY";

    /// <summary>
    /// Bật tiêu đề X-XSS-Protection để vô hiệu hóa trình lọc XSS có lỗi trên trình duyệt cũ.
    /// </summary>
    public bool EnableXssProtection { get; set; } = true;

    /// <summary>
    /// Giá trị cho tiêu đề X-XSS-Protection. Mặc định: 0 (chuẩn hiện đại).
    /// </summary>
    public string XssProtection { get; set; } = "0";

    /// <summary>
    /// Bật tiêu đề Referrer-Policy để kiểm soát dữ liệu nguồn giới thiệu chuyển tiếp.
    /// </summary>
    public bool EnableReferrerPolicy { get; set; } = true;

    /// <summary>
    /// Giá trị tiêu đề Referrer-Policy. Mặc định: strict-origin-when-cross-origin.
    /// </summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>
    /// Bật tiêu đề Content-Security-Policy (CSP) để giới hạn tài nguyên trình duyệt được tải.
    /// </summary>
    public bool EnableContentSecurityPolicy { get; set; } = true;

    /// <summary>
    /// Giá trị cho Content-Security-Policy. Mặc định: default-src 'self'; frame-ancestors 'none';
    /// </summary>
    public string ContentSecurityPolicy { get; set; } = "default-src 'self'; frame-ancestors 'none';";

    /// <summary>
    /// Bật tiêu đề Strict-Transport-Security (HSTS) để bắt buộc client kết nối qua HTTPS.
    /// </summary>
    public bool EnableStrictTransportSecurity { get; set; } = true;

    /// <summary>
    /// Thời gian tối đa lưu cache HSTS tính bằng giây. Mặc định: 31,536,000 giây (1 năm).
    /// </summary>
    public int HstsMaxAgeSeconds { get; set; } = 31536000;

    /// <summary>
    /// Áp dụng HSTS cho tất cả tên miền con (subdomains).
    /// </summary>
    public bool HstsIncludeSubDomains { get; set; } = true;
}

/// <summary>
/// Cấu hình thông tin danh tính của Gateway khi chuyển tiếp xuống downstream.
/// </summary>
public sealed class GatewayIdentityOptions
{
    /// <summary>
    /// Tên dịch vụ của Gateway sẽ được ghi đè vào tiêu đề X-Service-Name.
    /// Mặc định: "ApiGateway".
    /// </summary>
    public string ServiceName { get; set; } = "ApiGateway";
}
