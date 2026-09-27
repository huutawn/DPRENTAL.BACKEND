using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.MediaService.Domain;

public class MediaDbContextFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? throw new InvalidOperationException(
                "Set ConnectionStrings__Postgres in the environment before running EF migrations.");
        var optionsBuilder = new DbContextOptionsBuilder<MediaDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            b => b.MigrationsHistoryTable("__EFMigrationsHistory", MediaDbContext.SchemaName));

        return new MediaDbContext(optionsBuilder.Options);
    }
}
