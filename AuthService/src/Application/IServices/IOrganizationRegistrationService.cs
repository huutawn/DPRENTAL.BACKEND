using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Organization;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Tự đăng ký organization Supplier/Renter cho user đang đăng nhập; organization được tạo ở trạng thái Pending chờ Platform duyệt.</summary>
public interface IOrganizationRegistrationService
{
    Task<Result<OrganizationRegisteredResponse>> RegisterForExistingUserAsync(
        Guid actorUserId,
        OrganizationProfileRequest request,
        CancellationToken cancellationToken = default);
}
