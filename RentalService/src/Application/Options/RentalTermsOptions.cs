namespace Dprental.RentalService.Application.Options;

/// <summary>
/// Phiên bản Điều khoản thuê đang có hiệu lực. Renter phải đồng ý đúng phiên bản này khi checkout.
/// </summary>
public sealed class RentalTermsOptions
{
    public const string SectionName = "Rental:Terms";

    public string CurrentVersion { get; set; } = string.Empty;

    public string DocumentRef { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;
}
