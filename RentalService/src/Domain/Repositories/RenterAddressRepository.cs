using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RenterAddressRepository(RentalServiceDbContext dbContext) : IRenterAddressRepository
{
    public async Task<IReadOnlyList<RenterAddress>> ListByRenterAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        await dbContext.RenterAddresses
            .AsNoTracking()
            .Where(address => address.RenterId == renterId)
            .OrderByDescending(address => address.IsDefault)
            .ThenByDescending(address => address.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<RenterAddress?> GetByIdAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default) =>
        dbContext.RenterAddresses
            .AsNoTracking()
            .FirstOrDefaultAsync(address => address.Id == addressId && address.RenterId == renterId, cancellationToken);

    public async Task<IReadOnlyList<RenterAddress>> ListForUpdateAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        await dbContext.RenterAddresses
            .Where(address => address.RenterId == renterId)
            .OrderByDescending(address => address.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(RenterAddress address) => dbContext.RenterAddresses.Add(address);

    public void Remove(RenterAddress address) => dbContext.RenterAddresses.Remove(address);
}
