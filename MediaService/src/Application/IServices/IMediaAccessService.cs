using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common;

namespace Dprental.MediaService.Application.IServices;

public interface IMediaAccessService
{
    Task<Result<MediaVerificationDto>> VerifyMediaAsync(ServiceCaller caller, Guid mediaId, CancellationToken cancellationToken = default);
    Task<Result<MediaMetadataDto>> GetMetadataAsync(ServiceCaller caller, Guid mediaId, CancellationToken cancellationToken = default);
    Task<Result<MediaReadUrlDto>> CreatePrivateReadUrlAsync(ServiceCaller caller, Guid mediaId, MediaAccessRequest request, CancellationToken cancellationToken = default);
    Task<Result<MediaReadUrlDto>> CreateUploaderPreviewUrlAsync(string authenticatedUserId, Guid mediaId, CancellationToken cancellationToken = default);
    Task<Result<MediaReadUrlDto>> CreatePublicReadUrlAsync(Guid mediaId, bool download, CancellationToken cancellationToken = default);
    Task<Result<MediaVisibilityDto>> SetVisibilityAsync(ServiceCaller caller, Guid mediaId, ChangeMediaVisibilityRequest request, CancellationToken cancellationToken = default);
}
