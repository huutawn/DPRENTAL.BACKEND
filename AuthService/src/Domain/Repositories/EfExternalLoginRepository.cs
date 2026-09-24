using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Bỏ entity lỗi khỏi DbContext để lần SaveChanges sau không thử insert lại.
            dbContext.Entry(login).State = EntityState.Detached;
            throw new DuplicateExternalLoginException("Liên kết đăng nhập bên ngoài đã tồn tại.", exception);
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
