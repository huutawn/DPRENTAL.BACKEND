using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common;
using Dprental.MediaService.Domain.Entities;

namespace Dprental.MediaService.Application.IServices;

public interface IImageVariantService
{
    Task<Result<IReadOnlyList<MediaImageVariantDto>>> ProcessAndStoreVariantsAsync(
        MediaAsset asset,
        Stream sourceStream,
        MediaUploadSession? session = null,
        CancellationToken cancellationToken = default);
}
