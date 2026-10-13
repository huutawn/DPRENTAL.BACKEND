using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Options;

public sealed class PaymentMerchantOptions
{
    public const string SectionName = "Payment:Merchant";

    [Required(ErrorMessage = "Merchant Bank is required.")]
    public string Bank { get; set; } = string.Empty;

    [Required(ErrorMessage = "Merchant Account is required.")]
    public string Account { get; set; } = string.Empty;
}
