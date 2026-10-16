namespace Dprental.MediaService.Domain.Processing;

public record ImageVariantSpec(
    string Name,
    int MaxWidth,
    int Quality = 80);

public record ProcessedImageVariant(
    string Name,
    int Width,
    int Height,
    string ContentType,
    long SizeBytes,
    string ChecksumSha256,
    byte[] Data);
