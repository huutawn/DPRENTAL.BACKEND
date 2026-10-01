using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.CatalogService.Domain.Repositories;

/// <summary>
/// Tạo DbContext lúc design-time cho các lệnh <c>dotnet ef</c>.
/// </summary>
public sealed class CatalogServiceDbContextFactory : IDesignTimeDbContextFactory<CatalogServiceDbContext>
{
    public CatalogServiceDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? throw new InvalidOperationException(
                "Set ConnectionStrings__Postgres in the environment before running EF migrations.");
        var optionsBuilder = new DbContextOptionsBuilder<CatalogServiceDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            b => b.MigrationsHistoryTable(
                CatalogServiceDbContext.MigrationsHistoryTableName,
                CatalogServiceDbContext.SchemaName));

        return new CatalogServiceDbContext(optionsBuilder.Options);
    }
}
