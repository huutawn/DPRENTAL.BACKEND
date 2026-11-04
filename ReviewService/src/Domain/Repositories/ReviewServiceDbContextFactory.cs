using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Factory khởi tạo ReviewServiceDbContext tại thời điểm thiết kế (Design-time) phục vụ công cụ dotnet-ef migrations.
/// </summary>
public sealed class ReviewServiceDbContextFactory : IDesignTimeDbContextFactory<ReviewServiceDbContext>
{
    public ReviewServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ReviewServiceDbContext>();
        string designConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=review_service_design;Username=postgres;Password=postgres;Timeout=1";
        optionsBuilder.UseNpgsql(designConnectionString);

        return new ReviewServiceDbContext(optionsBuilder.Options);
    }
}
