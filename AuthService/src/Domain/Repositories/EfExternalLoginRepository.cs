using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Domain.Repositories;

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

    public async Task AddAndInvalidatePasswordAsync(
        ExternalLogin login,
        string unusablePasswordHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentException.ThrowIfNullOrWhiteSpace(unusablePasswordHash);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        User user = await dbContext.Users.SingleAsync(u => u.Id == login.UserId, cancellationToken);
        user.PasswordHash = unusablePasswordHash;
        user.HasPassword = false;

        List<Session> sessions = await dbContext.Sessions
            .Where(s => s.UserId == login.UserId && s.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (Session s in sessions)
        {
            s.RevokedAtUtc = utcNow;
        }

        List<RefreshToken> tokens = await dbContext.RefreshTokens
            .Where(t => t.UserId == login.UserId && !t.IsRevoked)
            .ToListAsync(cancellationToken);
        foreach (RefreshToken t in tokens)
        {
            t.IsRevoked = true;
            t.RevokedAtUtc = utcNow;
            t.RevokedReason = "ExternalLoginLinked";
        }

        dbContext.ExternalLogins.Add(login);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception)
        {
            // Transaction chưa commit sẽ rollback khi dispose; detach những gì vừa sửa để context dùng tiếp được.
            dbContext.Entry(login).State = EntityState.Detached;
            dbContext.Entry(user).State = EntityState.Detached;
            sessions.ForEach(s => dbContext.Entry(s).State = EntityState.Detached);
            tokens.ForEach(t => dbContext.Entry(t).State = EntityState.Detached);
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
