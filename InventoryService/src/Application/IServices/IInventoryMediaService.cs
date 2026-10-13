using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Hợp đồng service điều phối xác minh, công bố Public và dựng URL ổn định cho danh sách Media và Thumbnail.
/// </summary>
public interface IInventoryMediaService
{
    Task<Result<IReadOnlyList<MediaReferenceResponse>>> PreparePublicAsync(
        IReadOnlyList<Guid> mediaIds,
        Guid? thumbnailMediaId,
        IInventoryCallerContext caller,
        IReadOnlyCollection<Guid>? existingMediaIds = null,
        Guid? existingThumbnailMediaId = null,
        CancellationToken cancellationToken = default);
}
