using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Organization;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Tự đăng ký organization Supplier/Renter cho user đang đăng nhập; organization được tạo ở trạng thái Pending chờ Platform duyệt.</summary>
public interface IOrganizationRegistrationService
{
    Task<Result<OrganizationRegisteredResponse>> RegisterForExistingUserAsync(
        Guid actorUserId,
        OrganizationProfileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Owner sửa hồ sơ. Pending: sửa toàn bộ. Rejected: sửa toàn bộ và tự gửi lại (về Pending).
    /// Active: chỉ đổi liên hệ/hiển thị. Suspended: không cho sửa.
    /// </summary>
    Task<Result<OrganizationResponse>> UpdateProfileAsync(
        Guid actorUserId,
        Guid organizationId,
        UpdateOrganizationProfileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Owner nhắc Platform duyệt hồ sơ đang Pending đã quá hạn duyệt. Hồ sơ giữ nguyên nội dung và thứ tự nộp,
    /// chỉ được đưa lên đầu hàng chờ. Chưa đến hạn hoặc vừa nhắc xong thì trả lỗi.
    /// </summary>
    Task<Result<OrganizationResponse>> RemindReviewAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
