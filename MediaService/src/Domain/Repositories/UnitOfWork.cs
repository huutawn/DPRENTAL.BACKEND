using Dprental.MediaService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.MediaService.Domain.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly MediaDbContext _context;

    public UnitOfWork(MediaDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new MediaWriteConflictException("Media state changed concurrently.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new MediaWriteConflictException("Media idempotency key already exists.", ex);
        }
    }
}
