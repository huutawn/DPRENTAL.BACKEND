using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public sealed class CatalogServiceDbContext : DbContext
{
    public CatalogServiceDbContext(DbContextOptions<CatalogServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
