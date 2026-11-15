using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class DamageLevelRepository(RentalServiceDbContext dbContext) : IDamageLevelRepository
{
    public async Task<IReadOnlyList<DamageLevel>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.DamageLevels.AsNoTracking().OrderBy(d => d.Level).ToListAsync(cancellationToken);
}
