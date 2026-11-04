using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfOrganizationLookupRepository(AuthDbContext dbContext) : IOrganizationLookupRepository
{
    public Task<OrganizationPublicRecord?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Organizations.AsNoTracking().Where(o => o.Id == id)
            .Select(o => new OrganizationPublicRecord(o.Id, o.Name, o.Slug, o.Type, o.Status, o.ContactPhone, o.LogoMediaId))
            .SingleOrDefaultAsync(cancellationToken);

}
