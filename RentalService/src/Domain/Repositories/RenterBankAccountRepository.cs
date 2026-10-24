using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RenterBankAccountRepository(RentalServiceDbContext dbContext) : IRenterBankAccountRepository
{
    public async Task<IReadOnlyList<RenterBankAccount>> ListByRenterAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        await dbContext.RenterBankAccounts
            .AsNoTracking()
            .Where(account => account.RenterId == renterId)
            .OrderByDescending(account => account.IsDefault)
            .ThenByDescending(account => account.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<RenterBankAccount?> GetByIdAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default) =>
        dbContext.RenterBankAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(account => account.Id == bankAccountId && account.RenterId == renterId, cancellationToken);

    public async Task<IReadOnlyList<RenterBankAccount>> ListForUpdateAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        await dbContext.RenterBankAccounts
            .Where(account => account.RenterId == renterId)
            .OrderByDescending(account => account.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(RenterBankAccount bankAccount) => dbContext.RenterBankAccounts.Add(bankAccount);

    public void Remove(RenterBankAccount bankAccount) => dbContext.RenterBankAccounts.Remove(bankAccount);
}
