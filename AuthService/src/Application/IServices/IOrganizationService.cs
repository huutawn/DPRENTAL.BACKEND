using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Application.DTOs.Organization;

namespace Dprental.AuthService.Application.IServices;

public interface IOrganizationService
{
    /// <summary>Lấy chi tiết organization khi người gọi là Owner (GET /organizations/{id}).</summary>
    Task<Result<OrganizationResponse>> GetByIdAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Lấy chi tiết organization dành cho Admin (GET /admin/organizations/{id}).</summary>
    Task<Result<OrganizationResponse>> GetAdminByIdAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tổ chức của chính người gọi (quan hệ 1-1), hoặc null nếu họ chưa đăng ký. Chỉ đọc DB — không cấp URL xem
    /// giấy phép — để client gọi mỗi lần vào trang mà không phụ thuộc MediaService.
    /// </summary>
    Task<Result<OrganizationResponse?>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Danh sách mọi organization có phân trang dành cho Admin (GET /admin/organizations).</summary>
    Task<Result<PagedResult<OrganizationSummaryResponse>>> ListAsync(
        Guid actorUserId,
        PageRequest request,
        OrganizationStatus? status = null,
        OrganizationType? type = null,
        string? province = null,
        bool overdueOnly = false,
        bool remindedOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>Cập nhật hồ sơ liên hệ và hiển thị của organization dành cho Admin (PUT /admin/organizations/{id}).</summary>
    Task<Result> UpdateProfileAsync(Guid actorUserId, Guid organizationId, UpdateOrganizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Khóa organization kèm lý do dành cho Admin (POST /admin/organizations/{id}/suspend).</summary>
    Task<Result> SuspendAsync(Guid actorUserId, Guid organizationId, SuspendOrganizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Mở khóa organization dành cho Admin (POST /admin/organizations/{id}/activate).</summary>
    Task<Result> ActivateAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Lấy nhật ký lịch sử của organization có phân trang và bộ lọc tìm kiếm dành cho Admin (GET /admin/organizations/{id}/history).</summary>
    Task<Result<PagedResult<OrganizationHistoryResponse>>> GetHistoryAsync(
        Guid actorUserId,
        Guid organizationId,
        PageRequest request,
        string? action = null,
        OrganizationStatus? status = null,
        string? result = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>MVP: mỗi organization chỉ có Owner (người đăng ký), không có thành viên/lời mời.</summary>
    Task<Result<IReadOnlyList<MyOrganizationResponse>>> GetMyOrganizationsAsync(Guid userId, CancellationToken cancellationToken = default);
}
