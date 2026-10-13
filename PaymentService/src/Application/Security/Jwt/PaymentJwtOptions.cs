using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Security.Jwt;

public sealed class PaymentJwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    [Required(ErrorMessage = "JWT Issuer is required.")]
    public string Issuer { get; set; } = "dprental-authservice-dev";

    [Required(ErrorMessage = "ServiceAudience is required.")]
    public string ServiceAudience { get; set; } = "payment-api";

    [Required(ErrorMessage = "ClientAudience is required.")]
    public string ClientAudience { get; set; } = "dprental-clients";

    [Required(ErrorMessage = "JwksUri is required.")]
    public string JwksUri { get; set; } = "http://auth-service:8080/.well-known/jwks.json";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    [Range(1, 1440)]
    public int MaxStaleMinutes { get; set; } = 60;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;
}
