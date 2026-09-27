using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Organization;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Platform duyệt hoặc từ chối organization tự đăng ký đang ở trạng thái Pending.</summary>
public interface IOrganizationReviewService
{
    Task<Result<OrganizationResponse>> ApproveAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    Task<Result<OrganizationResponse>> RejectAsync(
        Guid actorUserId,
        Guid organizationId,
        RejectOrganizationRequest request,
        CancellationToken cancellationToken = default);
}
