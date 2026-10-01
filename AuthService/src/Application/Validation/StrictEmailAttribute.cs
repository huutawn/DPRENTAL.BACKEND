using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Dprental.AuthService.Application.Validation;

/// <summary>
/// Kiểm tra email theo RFC 5321/5322 (dạng dot-atom) và quy tắc "valid e-mail address" của HTML5, chặt hơn
/// <see cref="EmailAddressAttribute"/> (vốn chỉ cần một dấu @ ở giữa). Chỉ nhận ASCII; tên miền phải có ít nhất
/// một dấu chấm và đuôi (TLD) từ 2 chữ cái. Null/rỗng coi là hợp lệ — dùng kèm <see cref="RequiredAttribute"/>.
/// Chỉ dùng khi ghi dữ liệu (đăng ký, tạo user, email liên hệ); đăng nhập giữ kiểm tra lỏng để tài khoản cũ vẫn vào được.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed partial class StrictEmailAttribute : ValidationAttribute
{
    public const int MaxLength = 254;
    public const int MaxLocalPartLength = 64;
    public const int MaxLabelLength = 63;

    public StrictEmailAttribute() : base("Email không hợp lệ.") { }

    // Phần trước @: các ký tự atext của RFC 5322, ngăn cách bằng dấu chấm đơn (không đầu/cuối, không "..").
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPartRegex();

    // Một nhãn tên miền: chữ/số, có thể có "-" ở giữa.
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^[A-Za-z]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelDomainRegex();

    public override bool IsValid(object? value)
    {
        if (value is not string email || email.Length == 0)
        {
            return true;
        }

        return IsValidEmail(email.Trim());
    }

    public static bool IsValidEmail(string email)
    {
        if (email.Length > MaxLength)
        {
            return false;
        }

        int at = email.LastIndexOf('@');
        if (at <= 0 || at != email.IndexOf('@'))
        {
            return false;
        }

        string localPart = email[..at];
        string domain = email[(at + 1)..];
        if (localPart.Length > MaxLocalPartLength || !LocalPartRegex().IsMatch(localPart))
        {
            return false;
        }

        string[] labels = domain.Split('.');
        if (labels.Length < 2 || !TopLevelDomainRegex().IsMatch(labels[^1]))
        {
            return false;
        }

        return labels.All(label => label.Length <= MaxLabelLength && LabelRegex().IsMatch(label));
    }
}
