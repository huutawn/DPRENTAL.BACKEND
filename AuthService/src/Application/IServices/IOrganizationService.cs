using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Application.DTOs.Organization;

namespace Dprental.AuthService.Application.IServices;

public interface IOrganizationService
{
    Task<Result<OrganizationResponse>> GetByIdAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tổ chức của chính người gọi (quan hệ 1-1), hoặc null nếu họ chưa đăng ký. Chỉ đọc DB — không cấp URL xem
    /// giấy phép — để client gọi mỗi lần vào trang mà không phụ thuộc MediaService.
    /// </summary>
    Task<Result<OrganizationResponse?>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary><paramref name="status"/> chỉ dành cho Platform (hàng chờ duyệt); user thường truyền vào sẽ bị từ chối.</summary>
    Task<Result<PagedResult<OrganizationSummaryResponse>>> ListAsync(
        Guid actorUserId,
        PageRequest request,
        OrganizationStatus? status = null,
        CancellationToken cancellationToken = default);

    Task<Result> UpdateAsync(Guid actorUserId, Guid organizationId, UpdateOrganizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>MVP: mỗi organization chỉ có Owner (người đăng ký), không có thành viên/lời mời.</summary>
    Task<Result<IReadOnlyList<MyOrganizationResponse>>> GetMyOrganizationsAsync(Guid userId, CancellationToken cancellationToken = default);
}
