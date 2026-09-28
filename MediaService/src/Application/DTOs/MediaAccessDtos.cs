namespace Dprental.MediaService.Application.DTOs;

public sealed record ServiceCaller(string? Name, IReadOnlySet<string> Scopes)
{
    public bool HasScope(string scope) => Scopes?.Contains(scope) == true;
}

public sealed record MediaMetadataDto(
    Guid MediaId,
    string OriginalFileName,
    string? ContentType,
    long? SizeBytes,
    string Status,
    string Visibility,
    DateTimeOffset CreatedAtUtc);

public sealed record MediaAccessRequest(
    string Action,
    bool Download = false);

public sealed record MediaReadUrlDto(Guid MediaId, string Url, DateTimeOffset ExpiresAtUtc);

public sealed record ChangeMediaVisibilityRequest(
    string Visibility);

public sealed record MediaVisibilityDto(Guid MediaId, string Visibility);
