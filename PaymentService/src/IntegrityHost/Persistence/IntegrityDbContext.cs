using Dprental.PaymentService.IntegrityHost.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.IntegrityHost.Persistence;

public sealed class IntegrityDbContext : DbContext
{
    public IntegrityDbContext(DbContextOptions<IntegrityDbContext> options) : base(options)
    {
    }

    public DbSet<AttestationRecord> Attestations => Set<AttestationRecord>();
    public DbSet<StreamManifest> Manifests => Set<StreamManifest>();
    public DbSet<IntegrityIncident> Incidents => Set<IntegrityIncident>();
    public DbSet<EntityHeadRecord> EntityHeads => Set<EntityHeadRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AttestationRecord>(b =>
        {
            b.HasIndex(x => new { x.StreamId, x.Version }).IsUnique();
            b.HasIndex(x => x.EventId).IsUnique();
            b.HasIndex(x => x.ConfirmedAt);
        });

        modelBuilder.Entity<StreamManifest>(b =>
        {
            b.HasKey(x => x.StreamId);
        });

        modelBuilder.Entity<IntegrityIncident>(b =>
        {
            b.HasIndex(x => x.StreamId);
            b.HasIndex(x => new { x.Status, x.DetectedAt });
        });

        modelBuilder.Entity<EntityHeadRecord>(b =>
        {
            b.HasKey(x => new { x.EntityType, x.EntityId });
            b.HasIndex(x => x.LatestAttestationId);
        });
    }
}
