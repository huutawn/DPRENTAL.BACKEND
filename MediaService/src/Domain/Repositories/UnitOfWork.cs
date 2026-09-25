using Dprental.MediaService.Domain.IRepositories;

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
        return await _context.SaveChangesAsync(cancellationToken);
    }
}
