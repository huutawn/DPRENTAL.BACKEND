using System.ComponentModel.DataAnnotations;

namespace Dprental.ReviewService.Api.Security;

/// <summary>
/// Cấu hình xác thực JWT Bearer và JWKS cho ReviewService.
/// </summary>
public sealed class ReviewJwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    [Required(ErrorMessage = "Issuer JWT là bắt buộc.")]
    public string Issuer { get; set; } = "dprental-authservice-dev";

    [Required(ErrorMessage = "Audience JWT là bắt buộc.")]
    public string Audience { get; set; } = "dprental-clients";

    [Required(ErrorMessage = "JwksUri JWT là bắt buộc.")]
    public string JwksUri { get; set; } = "http://auth-service:8080/.well-known/jwks.json";

    [Range(1, 120, ErrorMessage = "TimeoutSeconds phải từ 1 đến 120.")]
    public int TimeoutSeconds { get; set; } = 10;

    [Range(1, 1440, ErrorMessage = "MaxStaleMinutes phải từ 1 đến 1440.")]
    public int MaxStaleMinutes { get; set; } = 60;
}
