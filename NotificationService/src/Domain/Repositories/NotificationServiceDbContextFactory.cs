using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class NotificationServiceDbContextFactory : IDesignTimeDbContextFactory<NotificationServiceDbContext>
{
    public NotificationServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NotificationServiceDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=dprental_notification;Username=postgres;Password=postgres");

        return new NotificationServiceDbContext(optionsBuilder.Options);
    }
}
