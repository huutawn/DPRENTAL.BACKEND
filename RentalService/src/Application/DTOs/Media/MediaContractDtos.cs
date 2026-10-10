using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Application.DTOs.Media;

/// <summary>
/// Verified media item details returned by MediaService GET /api/v1/media/{id}/verify.
/// Provider wire contract:
/// - uploadedByUserId: string (UUID representation)
/// - status: "PendingUpload" | "Ready" | "Rejected" | "Deleted"
/// - originalFileName: string?
/// - contentType: string?
/// - sizeBytes: long
/// - createdAtUtc: ISO 8601 DateTimeOffset
/// (Provider verify route strictly omits visibility).
/// </summary>
public sealed record MediaVerifyDto(
    [Required] Guid MediaId,
    [Required] string UploadedByUserId,
    [Required] string Status,
    string? OriginalFileName = null,
    string? ContentType = null,
    long SizeBytes = 0,
    DateTimeOffset CreatedAtUtc = default);

/// <summary>
/// Media metadata returned by MediaService GET /api/v1/media/{id}.
/// Provider wire contract returns visibility, status, and createdAtUtc (strictly omits uploader).
/// </summary>
public sealed record MediaMetadataDto(
    [Required] Guid MediaId,
    [Required] string Visibility,
    [Required] string Status,
    DateTimeOffset CreatedAtUtc = default);

/// <summary>
/// Request payload to generate private read access URL via POST /api/v1/media/{id}/access.
/// Provider contract accepts action ("read") and download flag.
/// NOTE: RentalService verifies viewer authorization; MediaService decides URL validity duration.
/// </summary>
public sealed record CreateMediaAccessRequest(
    [Required] string Action = "read",
    bool Download = false);

/// <summary>
/// Signed private read access descriptor returned by MediaService.
/// </summary>
public sealed record MediaAccessDto(
    [Required] Guid MediaId,
    [Required] string Url,
    [Required] DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Composite verified evidence item for application layer tracking.
/// </summary>
public sealed record VerifiedEvidenceMediaDto(
    Guid MediaId,
    Guid UploadedByUserId,
    string Status,
    string Visibility,
    string? OriginalFileName = null,
    string? ContentType = null);
