using System.ComponentModel.DataAnnotations;

namespace Dprental.AnalyticsService.Application.Security.Jwt;

/// <summary>
/// Cấu hình xác thực JWT do AuthService ký (RS256, public key từ JWKS). Giữ nguyên các khóa
/// Authentication:Jwt:{Issuer, Audience, JwksUri} đang dùng; thêm timeout, độ cũ cache và độ lệch đồng hồ.
/// </summary>
public sealed class AnalyticsJwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    [Required]
    public string Issuer { get; set; } = "dprental-authservice-dev";

    /// <summary>Audience của token người dùng (FE). Analytics không nhận token service.</summary>
    [Required]
    public string Audience { get; set; } = "dprental-clients";

    [Required]
    public string JwksUri { get; set; } = "http://auth-service:8080/.well-known/jwks.json";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    // AuthService lỗi tạm thời thì vẫn dùng key đã cache trong khoảng này
    [Range(1, 1440)]
    public int MaxStaleMinutes { get; set; } = 60;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;
}
