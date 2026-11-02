using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

public sealed class ReviewServiceDbContext : DbContext
{
    public ReviewServiceDbContext(DbContextOptions<ReviewServiceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
