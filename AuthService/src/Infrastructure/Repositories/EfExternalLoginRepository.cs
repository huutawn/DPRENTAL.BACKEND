using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Infrastructure.Repositories;

public sealed class EfExternalLoginRepository(AuthDbContext dbContext) : IExternalLoginRepository
{
    public Task<ExternalLogin?> FindAsync(string provider, string providerSubject, CancellationToken cancellationToken = default) =>
        dbContext.ExternalLogins
            .AsNoTracking()
            .Include(login => login.User)
            .SingleOrDefaultAsync(
                login => login.Provider == provider && login.ProviderSubject == providerSubject,
                cancellationToken);

    public async Task AddAsync(ExternalLogin login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);

        dbContext.ExternalLogins.Add(login);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(login).State = EntityState.Detached;
            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<ExternalLogin>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.ExternalLogins
            .AsNoTracking()
            .Where(login => login.UserId == userId)
            .OrderBy(login => login.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<bool> DeleteAsync(Guid userId, string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        ExternalLogin? login = await dbContext.ExternalLogins
            .SingleOrDefaultAsync(
                l => l.UserId == userId && l.Provider == provider,
                cancellationToken);

        if (login is null)
        {
            return false;
        }

        dbContext.ExternalLogins.Remove(login);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
