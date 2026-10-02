using System.ComponentModel.DataAnnotations;

namespace Dprental.InventoryService.Api.Security;

/// <summary>
/// Tùy chọn cấu hình JWT Authentication và kết nối JWKS tới AuthService.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    /// <summary>
    /// Issuer chuẩn phát hành JWT từ AuthService (ví dụ: auth-service hoặc http://localhost:5000).
    /// </summary>
    [Required(ErrorMessage = "Issuer của JWT không được để trống.")]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Audience cho Human users (mặc định: dprental-clients).
    /// </summary>
    [Required(ErrorMessage = "HumanAudience không được để trống.")]
    public string HumanAudience { get; set; } = "dprental-clients";

    /// <summary>
    /// Audience cho Service Principals (mặc định: inventory-api).
    /// </summary>
    [Required(ErrorMessage = "ServiceAudience không được để trống.")]
    public string ServiceAudience { get; set; } = "inventory-api";

    /// <summary>
    /// Endpoint JWKS public keys của AuthService (ví dụ: http://auth-service:5000/.well-known/jwks.json).
    /// </summary>
    [Required(ErrorMessage = "JwksUri không được để trống.")]
    public string JwksUri { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian chờ gọi HTTP JWKS (mặc định: 5 giây).
    /// </summary>
    [Range(1, 60, ErrorMessage = "TimeoutSeconds phải từ 1 đến 60 giây.")]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Thời gian cache JWKS keys trước khi cần refresh (mặc định: 5 phút).
    /// </summary>
    [Range(1, 60, ErrorMessage = "CacheDurationMinutes phải từ 1 đến 60 phút.")]
    public int CacheDurationMinutes { get; set; } = 5;

    /// <summary>
    /// Thời gian tối đa chấp nhận cache cũ khi AuthService tạm thời không khả dụng (mặc định: 10 phút).
    /// Không cho phép dùng cache vượt quá thời gian này.
    /// </summary>
    [Range(1, 120, ErrorMessage = "MaxStaleMinutes phải từ 1 đến 120 phút.")]
    public int MaxStaleMinutes { get; set; } = 10;
}
