using Microsoft.EntityFrameworkCore;

namespace Dprental.__SERVICE_NAME__.Domain.Repositories;

public sealed class __SERVICE_NAME__DbContext : DbContext
{
    public __SERVICE_NAME__DbContext(DbContextOptions<__SERVICE_NAME__DbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
