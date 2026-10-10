using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Api.Security;

public sealed class RentalJwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    [Required]
    public string Issuer { get; set; } = "dprental-authservice-dev";

    [Required]
    public string Audience { get; set; } = "dprental-clients";

    [Required]
    public string JwksUri { get; set; } = "http://auth-service:8080/.well-known/jwks.json";

    public int TimeoutSeconds { get; set; } = 10;

    public int MaxStaleMinutes { get; set; } = 60;
}
