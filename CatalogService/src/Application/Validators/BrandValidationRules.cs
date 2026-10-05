using System.Text;
using System.Text.RegularExpressions;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Validators;

/// <summary>
/// Các quy tắc kiểm tra và chuẩn hóa dữ liệu cho Brand.
/// </summary>
public static class BrandValidationRules
{
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxSlugLength = 150;

    private static readonly Regex SlugPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeCode(string? code) =>
        code?.Trim().ToUpperInvariant() ?? string.Empty;

    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string normalized = name.Normalize(NormalizationForm.FormC);
        return WhitespacePattern.Replace(normalized.Trim(), " ");
    }

    public static string NormalizeSlug(string? slug) =>
        slug?.Trim().ToLowerInvariant() ?? string.Empty;

    public static Result ValidateCode(string? rawCode, out string normalizedCode)
    {
        normalizedCode = string.Empty;

        if (rawCode != null && rawCode.Contains('\0'))
        {
            return Result.Failure(CatalogErrors.Brand.InvalidCode);
        }

        normalizedCode = NormalizeCode(rawCode);

        if (string.IsNullOrWhiteSpace(rawCode) || string.IsNullOrEmpty(normalizedCode) || normalizedCode.Length > MaxCodeLength)
        {
            return Result.Failure(CatalogErrors.Brand.InvalidCode);
        }

        return Result.Success();
    }

    public static Result ValidateName(string? rawName, out string normalizedName)
    {
        normalizedName = string.Empty;

        if (rawName != null && rawName.Contains('\0'))
        {
            return Result.Failure(CatalogErrors.Brand.InvalidName);
        }

        normalizedName = NormalizeName(rawName);

        if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrEmpty(normalizedName) || normalizedName.Length > MaxNameLength)
        {
            return Result.Failure(CatalogErrors.Brand.InvalidName);
        }

        return Result.Success();
    }

    public static Result ValidateSlug(string? rawSlug, out string normalizedSlug)
    {
        normalizedSlug = string.Empty;

        if (rawSlug != null && rawSlug.Contains('\0'))
        {
            return Result.Failure(CatalogErrors.Brand.InvalidSlug);
        }

        normalizedSlug = NormalizeSlug(rawSlug);

        if (string.IsNullOrWhiteSpace(rawSlug) ||
            string.IsNullOrEmpty(normalizedSlug) ||
            normalizedSlug.Length > MaxSlugLength ||
            !SlugPattern.IsMatch(normalizedSlug))
        {
            return Result.Failure(CatalogErrors.Brand.InvalidSlug);
        }

        return Result.Success();
    }

    public static Result ValidateLogoMediaId(Guid? logoMediaId, bool isSpecified)
    {
        if (isSpecified && logoMediaId.HasValue && logoMediaId.Value == Guid.Empty)
        {
            return Result.Failure(CatalogErrors.Brand.InvalidLogo);
        }

        return Result.Success();
    }
}
