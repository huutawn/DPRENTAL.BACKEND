using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfOrganizationLookupRepository(AuthDbContext dbContext) : IOrganizationLookupRepository
{
    public Task<OrganizationPublicRecord?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Organizations.AsNoTracking().Where(o => o.Id == id)
            .Select(o => new OrganizationPublicRecord(o.Id, o.Name, o.Slug, o.Type, o.Status, o.ContactPhone, o.LogoMediaId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OrganizationPublicRecord>> FindByOwnerAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && (m.Role.Name == "Owner" || m.Role.Name == "owner"))
            .OrderBy(m => m.OrganizationId).Take(2)
            .Select(m => new OrganizationPublicRecord(m.Organization.Id, m.Organization.Name, m.Organization.Slug,
                m.Organization.Type, m.Organization.Status, m.Organization.ContactPhone, m.Organization.LogoMediaId))
            .ToListAsync(cancellationToken);
}
