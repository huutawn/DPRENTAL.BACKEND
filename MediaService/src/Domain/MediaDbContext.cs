using Dprental.MediaService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.MediaService.Domain;

/// <summary>
/// DbContext cho MediaService quản lý các bảng trong schema "media".
/// </summary>
public class MediaDbContext : DbContext
{
    public const string SchemaName = "media";

    public MediaDbContext(DbContextOptions<MediaDbContext> options) : base(options)
    {
    }

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<MediaUploadSession> MediaUploadSessions => Set<MediaUploadSession>();
    public DbSet<MediaCleanupItem> MediaCleanupItems => Set<MediaCleanupItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MediaDbContext).Assembly);
    }
}
