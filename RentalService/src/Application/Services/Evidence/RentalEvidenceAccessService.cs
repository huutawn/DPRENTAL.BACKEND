using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.IServices.Evidence;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Evidence;

/// <summary>
/// Người dùng không gọi được MediaService để đọc ảnh riêng tư; Rental kiểm tra người xem thuộc khoản thuê
/// và ảnh thuộc bằng chứng của mục thuê, rồi mới xin MediaService cấp link đọc có hạn.
/// </summary>
public sealed class RentalEvidenceAccessService(
    IRentalOrderRepository orders,
    IRentalMediaClient mediaClient) : IRentalEvidenceAccessService
{
    // Trùng mã và nội dung với lỗi của AuthorizeEvidenceRead để mục thuê không tồn tại không phân biệt được với người ngoài.
    private static readonly Error EvidenceNotFound = Error.NotFound(
        RentalAuthorizationPolicy.ErrorCodes.EvidenceNotFound,
        "MediaId không thuộc bằng chứng của khoản thuê này.");

    public async Task<Result<MediaAccessDto>> CreateReadAccessAsync(
        Guid itemId,
        Guid mediaId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        RentalOrder? order = await orders.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        RentalItem? item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        if (order is null || item is null)
        {
            return EvidenceNotFound;
        }

        Result access = RentalAuthorizationPolicy.AuthorizeEvidenceRead(caller, order, item, mediaId);
        if (access.IsFailure)
        {
            return access.Error;
        }

        return await mediaClient.CreatePrivateReadAccessAsync(mediaId, new CreateMediaAccessRequest(), cancellationToken);
    }
}
