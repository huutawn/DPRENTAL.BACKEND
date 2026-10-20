using Dprental.MediaService.Domain.Processing;

namespace Dprental.MediaService.Application.Policies;

public static class ImageVariantPolicy
{
    public const int CurrentProcessingVersion = 1;
    public const int WebpQuality = 80;
    public const long MaxPixelLimit = 40_000_000;

    public const string Original = "original";

    public const string Thumbnail = "thumbnail";
    public const string Medium = "medium";
    public const string Large = "large";

    public const int ThumbnailMaxWidth = 320;
    public const int MediumMaxWidth = 768;
    public const int LargeMaxWidth = 1600;

    public static readonly IReadOnlyList<ImageVariantSpec> RequiredVariants =
    [
        new(Thumbnail, ThumbnailMaxWidth, WebpQuality),
        new(Medium, MediumMaxWidth, WebpQuality),
        new(Large, LargeMaxWidth, WebpQuality)
    ];

    public static bool IsProcessableImage(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        return string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "image/webp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalizeVariantName(string? variantName, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(variantName))
        {
            normalized = string.Empty;
            return false;
        }

        string trimmed = variantName.Trim().ToLowerInvariant();
        if (trimmed is Thumbnail or Medium or Large or Original)
        {
            normalized = trimmed;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static string BuildContentPath(Guid mediaId, string variantName) =>
        $"/api/v1/media/{mediaId:D}/content?variant={variantName}";
}
