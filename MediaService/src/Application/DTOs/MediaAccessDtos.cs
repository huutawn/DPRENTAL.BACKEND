namespace Dprental.MediaService.Application.DTOs;


public sealed record MediaMetadataDto(
    Guid MediaId,
    string OriginalFileName,
    string? ContentType,
    long? SizeBytes,
    string Status,
    string Visibility,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<MediaImageVariantDto>? ImageVariants = null)
{
    public IReadOnlyList<MediaImageVariantDto> ImageVariants { get; init; } = ImageVariants ?? [];
}

public sealed record MediaAccessRequest(
    string Action,
    bool Download = false,
    string? Variant = null);

public sealed record MediaReadUrlDto(Guid MediaId, string Url, DateTimeOffset ExpiresAtUtc);

public sealed record ChangeMediaVisibilityRequest(
    string Visibility);

public sealed record MediaVisibilityDto(Guid MediaId, string Visibility);
