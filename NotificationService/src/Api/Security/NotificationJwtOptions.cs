using System.ComponentModel.DataAnnotations;

namespace Dprental.NotificationService.Api.Security;

public sealed class NotificationJwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    public string JwksUri { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public int RefreshIntervalMinutes { get; set; } = 15;

    public int MaxStaleMinutes { get; set; } = 60;

    public int RefreshThrottleSeconds { get; set; } = 30;
}
