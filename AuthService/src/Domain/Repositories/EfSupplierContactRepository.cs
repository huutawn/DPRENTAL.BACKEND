using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfSupplierContactRepository(AuthDbContext dbContext) : ISupplierContactRepository
{
    public Task<SupplierContactRecord?> FindAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        dbContext.Organizations.AsNoTracking()
            .Where(org => org.Id == organizationId)
            .Select(org => new SupplierContactRecord(org.Name, org.ContactPhone, org.Type, org.Status))
            .SingleOrDefaultAsync(cancellationToken);
}
