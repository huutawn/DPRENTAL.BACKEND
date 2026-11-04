using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class NotificationServiceDbContextFactory : IDesignTimeDbContextFactory<NotificationServiceDbContext>
{
    public NotificationServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NotificationServiceDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=notification-design.invalid;Database=notification_design;Username=design;Timeout=1";
        optionsBuilder.UseNpgsql(connectionString);

        return new NotificationServiceDbContext(optionsBuilder.Options);
    }
}
