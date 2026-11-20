using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Evidence;

/// <summary>
/// Cấp link đọc tạm thời cho ảnh bằng chứng riêng tư (bàn giao, trả máy, khấu trừ, tranh chấp) của một mục thuê.
/// </summary>
public interface IRentalEvidenceAccessService
{
    Task<Result<MediaAccessDto>> CreateReadAccessAsync(
        Guid itemId,
        Guid mediaId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
