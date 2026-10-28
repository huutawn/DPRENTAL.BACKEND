using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Factory khởi tạo AnalyticsServiceDbContext tại thời điểm thiết kế (Design-time) phục vụ công cụ dotnet-ef migrations.
/// </summary>
public sealed class AnalyticsServiceDbContextFactory : IDesignTimeDbContextFactory<AnalyticsServiceDbContext>
{
    public AnalyticsServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AnalyticsServiceDbContext>();
        string designConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=analytics-design.invalid;Database=analytics_design;Username=design;Timeout=1";
        optionsBuilder.UseNpgsql(designConnectionString);

        return new AnalyticsServiceDbContext(optionsBuilder.Options);
    }
}
