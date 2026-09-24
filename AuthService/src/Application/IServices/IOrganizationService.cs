using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Organization;

namespace Dprental.AuthService.Application.IServices;

public interface IOrganizationService
{
    Task<Result<OrganizationResponse>> CreateAsync(Guid actorUserId, CreateOrganizationRequest request, CancellationToken cancellationToken = default);

    Task<Result<OrganizationResponse>> GetByIdAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<OrganizationResponse>>> ListAsync(Guid actorUserId, PageRequest request, CancellationToken cancellationToken = default);

    Task<Result> UpdateAsync(Guid actorUserId, Guid organizationId, UpdateOrganizationRequest request, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<OrganizationMemberResponse>>> GetMembersAsync(Guid actorUserId, Guid organizationId, PageRequest request, CancellationToken cancellationToken = default);

    Task<Result<OrganizationMemberResponse>> AddMemberAsync(Guid actorUserId, Guid organizationId, AddOrganizationMemberRequest request, CancellationToken cancellationToken = default);

    Task<Result> PatchMemberAsync(Guid actorUserId, Guid organizationId, Guid memberId, PatchOrganizationMemberRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteMemberAsync(Guid actorUserId, Guid organizationId, Guid memberId, CancellationToken cancellationToken = default);

    Task<Result> AcceptInvitationAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MyOrganizationResponse>>> GetMyOrganizationsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OrganizationInvitationResponse>>> GetMyInvitationsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Result> DeclineInvitationAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);

    Task<Result> LeaveAsync(Guid actorUserId, Guid organizationId, CancellationToken cancellationToken = default);
}
