using Dprental.NotificationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class NotificationServiceDbContext : DbContext
{
    public NotificationServiceDbContext(DbContextOptions<NotificationServiceDbContext> options) : base(options)
    {
    }

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationInboxReceipt> InboxReceipts => Set<NotificationInboxReceipt>();
    public DbSet<NotificationDeliveryJob> DeliveryJobs => Set<NotificationDeliveryJob>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<NotificationAggregateWatermark> AggregateWatermarks => Set<NotificationAggregateWatermark>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("notification");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationServiceDbContext).Assembly);
    }
}
