using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dprental.AuthService.Application.Rules;

/// <summary>Quy tắc slug của organization: chữ thường, số và dấu gạch ngang đơn.</summary>
internal static partial class OrganizationSlug
{
    internal const int MaxLength = 100;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugCharsRegex();

    internal static bool IsCanonical(string slug) =>
        slug.Length <= MaxLength && CanonicalRegex().IsMatch(slug);

    /// <summary>Sinh slug từ tên tiếng Việt: bỏ dấu, "đ" thành "d", ký tự khác chữ/số thành "-".</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Slug theo quy ước là chữ thường.")]
    internal static string FromName(string name)
    {
        string decomposed = name.Trim().ToLowerInvariant()
            .Replace('đ', 'd')
            .Normalize(NormalizationForm.FormD);

        StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        string slug = NonSlugCharsRegex().Replace(builder.ToString(), "-").Trim('-');
        if (slug.Length > MaxLength)
        {
            slug = slug[..MaxLength].TrimEnd('-');
        }

        return slug;
    }

    /// <summary>Thêm hậu tố ngẫu nhiên khi slug tự sinh đã bị dùng, vẫn giữ trong giới hạn độ dài.</summary>
    internal static string WithRandomSuffix(string slug)
    {
        string suffix = Guid.NewGuid().ToString("N")[..6];
        int maxBaseLength = MaxLength - suffix.Length - 1;
        string baseSlug = slug.Length > maxBaseLength ? slug[..maxBaseLength].TrimEnd('-') : slug;
        return baseSlug.Length == 0 ? suffix : $"{baseSlug}-{suffix}";
    }
}
