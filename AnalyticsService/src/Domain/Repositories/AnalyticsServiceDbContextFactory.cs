using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Chỉ dùng cho lệnh dotnet ef lúc sinh migration; không dùng khi ứng dụng chạy.
/// </summary>
public sealed class AnalyticsServiceDbContextFactory : IDesignTimeDbContextFactory<AnalyticsServiceDbContext>
{
    public AnalyticsServiceDbContext CreateDbContext(string[] args)
    {
        // Sinh migration không kết nối DB, nên chuỗi này chỉ cần đúng định dạng.
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=analyticsservice";

        DbContextOptions<AnalyticsServiceDbContext> options = new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AnalyticsServiceDbContext(options);
    }
}