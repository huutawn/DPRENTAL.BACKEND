using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.MediaService.Domain;

public class MediaDbContextFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MediaDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=127.0.0.1;Port=5434;Database=mediaservice;Username=mediaservice;Password=mediasecret",
            b => b.MigrationsHistoryTable("__EFMigrationsHistory", MediaDbContext.SchemaName));

        return new MediaDbContext(optionsBuilder.Options);
    }
}
