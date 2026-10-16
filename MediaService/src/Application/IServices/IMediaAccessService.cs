using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common;

namespace Dprental.MediaService.Application.IServices;

public interface IMediaAccessService
{
    Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default);
    Task<Result<MediaMetadataDto>> GetMetadataAsync(Guid mediaId, CancellationToken cancellationToken = default);
    Task<Result<MediaReadUrlDto>> CreatePrivateReadUrlAsync(Guid mediaId, MediaAccessRequest request, CancellationToken cancellationToken = default);
    Task<Result<MediaReadUrlDto>> CreateUploaderPreviewUrlAsync(string authenticatedUserId, Guid mediaId, CancellationToken cancellationToken = default);
    Task<Result<MediaReadUrlDto>> CreatePublicReadUrlAsync(Guid mediaId, bool download, CancellationToken cancellationToken = default);
    Task<Result<MediaVariantContentDto>> GetPublicVariantContentAsync(Guid mediaId, string? variantName, string? ifNoneMatch = null, CancellationToken cancellationToken = default);
    Task<Result<MediaVisibilityDto>> SetVisibilityAsync(Guid mediaId, ChangeMediaVisibilityRequest request, CancellationToken cancellationToken = default);
}
