using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class AnalyticsServiceDbContext : DbContext
{
    public AnalyticsServiceDbContext(DbContextOptions<AnalyticsServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
