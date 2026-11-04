using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Clients;

public sealed record OrganizationMembershipSnapshot(Guid OrganizationId, string RoleName, int Type, int OrganizationStatus, int MembershipStatus);

public interface IOrganizationMembershipClient
{
    Task<Result<IReadOnlyList<OrganizationMembershipSnapshot>>> GetCurrentAsync(CancellationToken cancellationToken = default);
}
