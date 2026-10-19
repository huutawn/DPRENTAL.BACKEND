using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class RecommendationServiceDbContext : DbContext
{
    public RecommendationServiceDbContext(DbContextOptions<RecommendationServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
