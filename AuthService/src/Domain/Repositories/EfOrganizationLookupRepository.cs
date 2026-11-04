using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfOrganizationLookupRepository(AuthDbContext dbContext) : IOrganizationLookupRepository
{
    public async Task<IReadOnlyList<Guid>> FindActiveSupplierIdsByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == ownerUserId && m.Status == MembershipStatus.Active &&
                (m.Role.Name == "Owner" || m.Role.Name == "owner") &&
                m.Organization.Type == OrganizationType.Supplier && m.Organization.Status == OrganizationStatus.Active)
            .Select(m => m.OrganizationId).Distinct().Take(2).ToListAsync(cancellationToken);
    public Task<OrganizationPublicRecord?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Organizations.AsNoTracking().Where(o => o.Id == id)
            .Select(o => new OrganizationPublicRecord(o.Id, o.Name, o.Slug, o.Type, o.Status, o.ContactPhone, o.LogoMediaId))
            .SingleOrDefaultAsync(cancellationToken);

}
