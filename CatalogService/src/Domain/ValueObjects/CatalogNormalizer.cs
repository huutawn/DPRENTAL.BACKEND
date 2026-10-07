using System.Text.RegularExpressions;

namespace Dprental.CatalogService.Domain.ValueObjects;

public static partial class CatalogNormalizer
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Trim, gộp khoảng trắng, chữ thường. Giữ nguyên dấu tiếng Việt để khớp câu SQL backfill trong migration.</summary>
    public static string NormalizeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Whitespace().Replace(value.Trim(), " ").ToLowerInvariant();
    }
}