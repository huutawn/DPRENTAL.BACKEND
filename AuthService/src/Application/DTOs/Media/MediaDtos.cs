namespace Dprental.AuthService.Application.DTOs.Media;

public sealed record MediaVerificationDto(
    Guid MediaId,
    string Status,
    string UploadedByUserId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);

public sealed record MediaReadUrlDto(
    Guid MediaId,
    string Url,
    DateTimeOffset ExpiresAtUtc);

public sealed record MediaVisibilityDto(
    Guid MediaId,
    string Visibility);

public sealed record MediaAccessRequest(
    string Action,
    bool Download = false);

public sealed record ChangeMediaVisibilityRequest(
    string Visibility);
