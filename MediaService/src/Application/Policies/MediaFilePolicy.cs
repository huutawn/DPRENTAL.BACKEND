namespace Dprental.MediaService.Application.Policies;

/// <summary>Giới hạn kỹ thuật cho các định dạng file MediaService hỗ trợ.</summary>
public static class MediaFilePolicy
{
    public const long TenMegabytes = 10L * 1024 * 1024;
    public const long TwentyMegabytes = 20L * 1024 * 1024;

    public static long? MaxSizeBytesFor(string? contentType) => contentType?.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/png" or "image/webp" => TenMegabytes,
        "application/pdf" => TwentyMegabytes,
        _ => null
    };

    public static bool IsSizeAllowed(string? contentType, long sizeBytes) =>
        sizeBytes > 0 && MaxSizeBytesFor(contentType) is long maximum && sizeBytes <= maximum;
}
