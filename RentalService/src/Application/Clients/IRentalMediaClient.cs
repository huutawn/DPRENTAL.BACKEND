using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// External client contract between RentalService and MediaService (v1).
/// Supports media verification, metadata retrieval, and private evidence read URL generation.
/// Uses active MediaService routes:
/// - GET /api/v1/media/{id}/verify
/// - GET /api/v1/media/{id}
/// - POST /api/v1/media/{id}/access
/// </summary>
public interface IRentalMediaClient
{
    /// <summary>
    /// Verifies media existence, processing status, and uploadedByUserId (GET /api/v1/media/{id}/verify).
    /// </summary>
    Task<Result<MediaVerifyDto>> VerifyMediaAsync(
        Guid mediaId,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves media metadata including visibility (GET /api/v1/media/{id}).
    /// </summary>
    Task<Result<MediaMetadataDto>> GetMetadataAsync(
        Guid mediaId,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a signed private read access URL for an authorized viewer (POST /api/v1/media/{id}/access).
    /// Called only after RentalService has confirmed viewer authorization (renter, supplier, or admin).
    /// </summary>
    Task<Result<MediaAccessDto>> CreatePrivateReadAccessAsync(
        Guid mediaId,
        CreateMediaAccessRequest request,
        CancellationToken ct = default);
}
