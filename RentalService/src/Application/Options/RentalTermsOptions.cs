using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Application.Options;

/// <summary>
/// Phiên bản Điều khoản thuê đang có hiệu lực. Renter phải đồng ý đúng phiên bản này khi checkout.
/// </summary>
/// <remarks>
/// Program bind từ Rental:Terms và kiểm tra DataAnnotations lúc startup.
/// Checkout dùng phiên bản hiện hành để kiểm tra chấp nhận điều khoản và lưu bằng chứng cùng snapshot của đơn.
/// </remarks>
public sealed class RentalTermsOptions
{
    public const string SectionName = "Rental:Terms";

    /// <summary>Phiên bản điều khoản mà renter phải chấp nhận khi checkout.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Rental:Terms:CurrentVersion is required.")]
    [StringLength(64, ErrorMessage = "Rental:Terms:CurrentVersion must not exceed 64 characters.")]
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>Tham chiếu tới tài liệu điều khoản để lưu bằng chứng phiên bản đã chấp nhận.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Rental:Terms:DocumentRef is required.")]
    [StringLength(256, ErrorMessage = "Rental:Terms:DocumentRef must not exceed 256 characters.")]
    public string DocumentRef { get; set; } = string.Empty;

    /// <summary>Dấu vân tay nội dung điều khoản, giúp đối chiếu bản đã được renter chấp nhận.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Rental:Terms:ContentHash is required.")]
    [StringLength(128, ErrorMessage = "Rental:Terms:ContentHash must not exceed 128 characters.")]
    public string ContentHash { get; set; } = string.Empty;
}
