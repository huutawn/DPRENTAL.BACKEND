using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Options;

public sealed class RentalServiceApiKeyOptions
{
    public const string SectionName = "InternalAuthentication";

    [Required]
    [MinLength(32)]
    public string RentalApiKey { get; set; } = string.Empty;
}
