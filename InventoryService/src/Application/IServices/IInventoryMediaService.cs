using Dprental.InventoryService.Domain.Clients.Media;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Hợp đồng service điều phối xác minh, công bố Public và dựng URL ổn định cho Media.
/// </summary>
public interface IInventoryMediaService
{
    Task<Result<MediaReferenceResponse?>> PreparePublicAsync(
        Guid? mediaId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
}
