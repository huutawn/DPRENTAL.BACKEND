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
    /// Lưu nháp hồ sơ tổ chức (Draft). Nếu chưa có thì tạo mới, nếu đã có (Draft/Rejected) thì cập nhật.
    /// Không bắt buộc thông tin cá nhân của User phải hoàn tất, không bắt buộc điền đủ thông tin pháp lý ngay.
    /// </summary>
    Task<Result<OrganizationResponse>> SaveDraftAsync(
        Guid actorUserId,
        OrganizationDraftRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Nộp hồ sơ nháp lên Platform duyệt. Kiểm tra đầy đủ điều kiện bắt buộc và chuyển sang Pending.
    /// </summary>
    Task<Result<OrganizationResponse>> SubmitDraftAsync(
        Guid actorUserId,
        Guid organizationId,
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
