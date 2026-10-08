using Dprental.RentalService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public class RentalServiceDbContext : DbContext
{
    public RentalServiceDbContext(DbContextOptions<RentalServiceDbContext> options) : base(options)
    {
    }

    public DbSet<RentalCart> RentalCarts => Set<RentalCart>();
    public DbSet<RentalCartItem> RentalCartItems => Set<RentalCartItem>();
    public DbSet<RentalOrder> RentalOrders => Set<RentalOrder>();
    public DbSet<RentalItem> RentalItems => Set<RentalItem>();
    public DbSet<RentalUnitAssignment> RentalUnitAssignments => Set<RentalUnitAssignment>();
    public DbSet<RentalStateTransition> RentalStateTransitions => Set<RentalStateTransition>();
    public DbSet<RentalTermsAcceptance> RentalTermsAcceptances => Set<RentalTermsAcceptance>();
    public DbSet<HandoverRecord> HandoverRecords => Set<HandoverRecord>();
    public DbSet<ReturnRecord> ReturnRecords => Set<ReturnRecord>();
    public DbSet<DeductionProposal> DeductionProposals => Set<DeductionProposal>();
    public DbSet<Dispute> Disputes => Set<Dispute>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("rental");

        // RentalCart & RentalCartItem
        modelBuilder.Entity<RentalCart>(b =>
        {
            b.HasMany(c => c.Items)
                .WithOne(i => i.RentalCart)
                .HasForeignKey(i => i.RentalCartId)
                .OnDelete(DeleteBehavior.Restrict);

            b.Property(c => c.Version)
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<RentalCartItem>(b =>
        {
            b.HasIndex(i => new { i.RentalCartId, i.EquipmentListingId, i.ExpectedStartDate, i.ExpectedEndDate })
                .IsUnique();
        });

        // RentalOrder & Children
        modelBuilder.Entity<RentalOrder>(b =>
        {
            b.HasIndex(o => o.OrderCode)
                .IsUnique();

            b.HasIndex(o => o.CheckoutOperationId)
                .IsUnique();

            b.Property(o => o.Version)
                .IsConcurrencyToken();

            b.HasOne(o => o.TermsAcceptance)
                .WithOne(t => t.RentalOrder)
                .HasForeignKey<RentalTermsAcceptance>(t => t.RentalOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(o => o.Items)
                .WithOne(i => i.RentalOrder)
                .HasForeignKey(i => i.RentalOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(o => o.StateTransitions)
                .WithOne(t => t.RentalOrder)
                .HasForeignKey(t => t.RentalOrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // RentalItem
        modelBuilder.Entity<RentalItem>(b =>
        {
            b.Property(i => i.DailyRateSnapshot)
                .HasPrecision(18, 2);

            b.Property(i => i.DepositPerUnitSnapshot)
                .HasPrecision(18, 2);

            b.Property(i => i.RentalAmount)
                .HasPrecision(18, 2);

            b.Property(i => i.DepositAmount)
                .HasPrecision(18, 2);

            b.Property(i => i.FinalDeductionAmount)
                .HasPrecision(18, 2);

            b.HasMany(i => i.UnitAssignments)
                .WithOne(u => u.RentalItem)
                .HasForeignKey(u => u.RentalItemId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(i => i.HandoverRecord)
                .WithOne(h => h.RentalItem)
                .HasForeignKey<HandoverRecord>(h => h.RentalItemId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(i => i.ReturnRecords)
                .WithOne(r => r.RentalItem)
                .HasForeignKey(r => r.RentalItemId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(i => i.DeductionProposal)
                .WithOne(p => p.RentalItem)
                .HasForeignKey<DeductionProposal>(p => p.RentalItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // RentalUnitAssignment
        modelBuilder.Entity<RentalUnitAssignment>(b =>
        {
            b.HasIndex(u => new { u.RentalItemId, u.AssetId })
                .IsUnique();

            b.HasIndex(u => new { u.RentalItemId, u.AllocationId })
                .IsUnique();

            b.Property(u => u.ProposedDeductionAmount)
                .HasPrecision(18, 2);

            b.HasOne(u => u.HandoverRecord)
                .WithMany(h => h.Assignments)
                .HasForeignKey(u => u.HandoverRecordId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(u => u.ReturnRecord)
                .WithMany(r => r.Assignments)
                .HasForeignKey(u => u.ReturnRecordId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(u => u.DeductionProposal)
                .WithMany(p => p.Assignments)
                .HasForeignKey(u => u.DeductionProposalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // DeductionProposal & Dispute
        modelBuilder.Entity<DeductionProposal>(b =>
        {
            b.HasOne(p => p.Dispute)
                .WithOne(d => d.DeductionProposal)
                .HasForeignKey<Dispute>(d => d.DeductionProposalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Dispute>(b =>
        {
            b.Property(d => d.FinalDeductionAmount)
                .HasPrecision(18, 2);
        });
    }
}
