using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IOrganizationMembershipRepository
{
    Task<OrganizationMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default);

    Task<OrganizationMembership?> FindMemberAsync(Guid organizationId, Guid memberOrUserId, CancellationToken cancellationToken = default);

    Task<PagedResult<OrganizationMembership>> GetMembersAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default);

    Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default);

    Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(OrganizationMembership membership, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid membershipId, CancellationToken cancellationToken = default);

    Task<int> CountOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
