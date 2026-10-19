using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.RecommendationService.Domain.Repositories;

/// <summary>
/// Factory khởi tạo RecommendationServiceDbContext tại thời điểm thiết kế (Design-time) phục vụ công cụ dotnet-ef migrations.
/// </summary>
public sealed class RecommendationServiceDbContextFactory : IDesignTimeDbContextFactory<RecommendationServiceDbContext>
{
    public RecommendationServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RecommendationServiceDbContext>();
        string designConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=recommendation-design.invalid;Database=recommendation_design;Username=design;Timeout=1";
        optionsBuilder.UseNpgsql(designConnectionString);

        return new RecommendationServiceDbContext(optionsBuilder.Options);
    }
}
