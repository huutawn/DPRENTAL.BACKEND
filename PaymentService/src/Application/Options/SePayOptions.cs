using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Options;

public sealed class SePayOptions
{
    public const string SectionName = "Payment:SePay";

    [Required(ErrorMessage = "SePay ApiKey is required.")]
    public string ApiKey { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string AccountNumber { get; set; } = string.Empty;

    public Dictionary<string, string> GatewayMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
