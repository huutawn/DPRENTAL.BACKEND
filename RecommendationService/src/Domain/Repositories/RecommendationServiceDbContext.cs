using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class RecommendationServiceDbContext : DbContext
{
    public RecommendationServiceDbContext(DbContextOptions<RecommendationServiceDbContext> options) : base(options)
    {
    }
}
