using System.Text;
using System.Text.RegularExpressions;

namespace Dprental.CatalogService.Common.Utils;

public static class SlugHelper
{
    public static string GenerateSlug(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return string.Empty;

        string str = RemoveDiacritics(phrase).ToLowerInvariant();

        // Xóa các ký tự không hợp lệ
        str = Regex.Replace(str, @"[^a-z0-9\s-]", "");
        // Chuyển nhiều khoảng trắng thành 1 khoảng trắng
        str = Regex.Replace(str, @"\s+", " ").Trim();
        // Thay khoảng trắng bằng dấu gạch ngang
        str = Regex.Replace(str, @"\s", "-");

        return str;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

        for (int i = 0; i < normalizedString.Length; i++)
        {
            char c = normalizedString[i];
            var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            // Xử lý riêng chữ đ/Đ của tiếng Việt
            .Replace("đ", "d").Replace("Đ", "D");
    }
}