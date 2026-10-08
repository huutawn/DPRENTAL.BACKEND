using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RentalServiceDbContext : DbContext
{
    public RentalServiceDbContext(DbContextOptions<RentalServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
