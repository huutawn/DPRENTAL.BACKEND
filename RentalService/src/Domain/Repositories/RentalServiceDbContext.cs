using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dprental.RentalService.Domain.Repositories;

public class RentalServiceDbContext : DbContext, IUnitOfWork
{
    public bool HasActiveTransaction => Database.CurrentTransaction is not null;
    public RentalServiceDbContext(DbContextOptions<RentalServiceDbContext> options) : base(options)
    {
    }

    public DbSet<RentalCart> RentalCarts => Set<RentalCart>();
    public DbSet<RentalCartItem> RentalCartItems => Set<RentalCartItem>();
    public DbSet<RentalOrder> RentalOrders => Set<RentalOrder>();
    public DbSet<RentalSubOrder> RentalSubOrders => Set<RentalSubOrder>();
    public DbSet<RentalItem> RentalItems => Set<RentalItem>();
    public DbSet<RentalUnitAssignment> RentalUnitAssignments => Set<RentalUnitAssignment>();
    public DbSet<RentalStateTransition> RentalStateTransitions => Set<RentalStateTransition>();
    public DbSet<RentalTermsAcceptance> RentalTermsAcceptances => Set<RentalTermsAcceptance>();
    public DbSet<HandoverRecord> HandoverRecords => Set<HandoverRecord>();
    public DbSet<ReturnRecord> ReturnRecords => Set<ReturnRecord>();
    public DbSet<DeductionProposal> DeductionProposals => Set<DeductionProposal>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<RentalOperationReceipt> RentalOperationReceipts => Set<RentalOperationReceipt>();
    public DbSet<RentalInboxMessage> RentalInboxMessages => Set<RentalInboxMessage>();
    public DbSet<RentalOutboxMessage> RentalOutboxMessages => Set<RentalOutboxMessage>();
    public DbSet<RentalCheckoutOperation> RentalCheckoutOperations => Set<RentalCheckoutOperation>();
    public DbSet<RenterAddress> RenterAddresses => Set<RenterAddress>();

    public void DiscardChanges() => ChangeTracker.Clear();

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (Database.IsRelational())
        {
            var tx = await Database.BeginTransactionAsync(cancellationToken);
            return new RelationalUnitOfWorkTransaction(tx);
        }

        return new NoOpUnitOfWorkTransaction();
    }

    private sealed class RelationalUnitOfWorkTransaction : IUnitOfWorkTransaction
    {
        private readonly IDbContextTransaction _tx;

        public RelationalUnitOfWorkTransaction(IDbContextTransaction tx)
        {
            _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => _tx.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => _tx.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => _tx.DisposeAsync();
    }

    private sealed class NoOpUnitOfWorkTransaction : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("rental");

        // Bộ đếm cho mã đơn hàng DH-YYYY-NNNNNN.
        modelBuilder.HasSequence<long>("order_code_seq", "rental");
        modelBuilder.HasSequence<long>("rental_outbox_sequence", "rental");

        // RentalCart & RentalCartItem
        modelBuilder.Entity<RentalCart>(b =>
        {
            // Dòng bị gỡ khỏi giỏ (RemoveItem, ClearItems, CompleteCheckout) phải được EF xóa;
            // ClientCascade làm việc đó phía ứng dụng và giữ nguyên ràng buộc FK trong database.
            b.HasMany(c => c.Items)
                .WithOne(i => i.RentalCart)
                .HasForeignKey(i => i.RentalCartId)
                .OnDelete(DeleteBehavior.ClientCascade);

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

            b.HasMany(o => o.SubOrders)
                .WithOne(s => s.RentalOrder)
                .HasForeignKey(s => s.RentalOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(o => o.StateTransitions)
                .WithOne(t => t.RentalOrder)
                .HasForeignKey(t => t.RentalOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(o => o.OperationReceipts)
                .WithOne(r => r.RentalOrder)
                .HasForeignKey(r => r.RentalOrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // RentalSubOrder: phần của đơn thuộc về một nhà cung cấp
        modelBuilder.Entity<RentalSubOrder>(b =>
        {
            b.HasMany(s => s.Items)
                .WithOne(i => i.RentalSubOrder)
                .HasForeignKey(i => i.RentalSubOrderId)
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

            b.Property(i => i.PickupLocationSnapshot)
                .HasMaxLength(RentalItem.MaxPickupLocationSnapshotLength);

            b.HasIndex(i => i.SupplierDecisionBy);

            // Admin tìm đơn theo thiết bị.
            b.HasIndex(i => i.EquipmentListingId);

            b.Property(i => i.SupplierDecisionNote)
                .HasMaxLength(2000);

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

            // Admin tìm đơn theo máy cụ thể; index (RentalItemId, AssetId) không dùng được khi chỉ lọc AssetId.
            b.HasIndex(u => u.AssetId);

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

            b.Property(u => u.HandoverMediaIds).HasField("_handoverMediaIds");
            b.Property(u => u.ReturnMediaIds).HasField("_returnMediaIds");
            b.Property(u => u.DeductionMediaIds).HasField("_deductionMediaIds");
        });

        // DeductionProposal & Dispute
        modelBuilder.Entity<DeductionProposal>(b =>
        {
            b.Property(p => p.TotalProposedAmount).HasPrecision(18, 2);
            b.Property(p => p.EvidenceMediaIds).HasField("_evidenceMediaIds");
            b.HasOne(p => p.Dispute)
                .WithOne(d => d.DeductionProposal)
                .HasForeignKey<Dispute>(d => d.DeductionProposalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Dispute>(b =>
        {
            b.Property(d => d.FinalDeductionAmount)
                .HasPrecision(18, 2);
            b.Property(d => d.MediaIds).HasField("_mediaIds");
            b.Property(d => d.ResolutionMediaIds).HasField("_resolutionMediaIds");
        });

        modelBuilder.Entity<RentalStateTransition>(b =>
        {
            b.Property(t => t.Id).ValueGeneratedNever();
            b.HasIndex(t => new { t.RentalItemId, t.Action, t.OccurredAt });
        });

        modelBuilder.Entity<RentalOperationReceipt>(b =>
        {
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.IsOutcomeUncertain).HasDefaultValue(false);
            b.Property(r => r.RefundCompletionJson);
            b.HasIndex(r => new { r.RentalOrderId, r.OperationId }).IsUnique();
            b.HasIndex(r => new { r.Status, r.NextRetryAt });
            b.HasIndex(r => new { r.Action, r.Status });
            b.HasIndex(r => r.TargetItemId);
            b.HasIndex(r => r.PrerequisiteOperationId);
        });

        modelBuilder.Entity<RentalInboxMessage>(b =>
        {
            b.Property(m => m.Id).ValueGeneratedNever();
            b.Property(m => m.KafkaTopic).HasMaxLength(256);
            b.Property(m => m.KafkaPartition);
            b.Property(m => m.KafkaOffset);
            b.HasIndex(m => m.MessageId).IsUnique();
            b.HasIndex(m => m.Status);
            b.HasIndex(m => new { m.Status, m.NextRetryAt, m.ReceivedAt });
        });
        modelBuilder.Entity<RentalOutboxMessage>(b =>
        {
            b.Property(m => m.Id).ValueGeneratedNever();
            b.Property(m => m.Sequence).HasDefaultValueSql("nextval('rental.rental_outbox_sequence')");
            b.HasIndex(m => new { m.Status, m.ScheduledAt, m.CreatedAt });
            b.HasIndex(m => new { m.Status, m.ScheduledAt, m.LockedUntilUtc });
            b.HasIndex(m => m.Status);
            b.HasIndex(m => new { m.Topic, m.PartitionKey, m.Sequence, m.Status });
        });

        modelBuilder.Entity<RentalCheckoutOperation>(b =>
        {
            b.HasKey(o => o.OperationId);
            b.Property(o => o.OperationId).HasMaxLength(128);
            b.Property(o => o.RequestFingerprint).HasMaxLength(64);
            b.HasIndex(o => o.RenterUserId);
            b.HasIndex(o => new { o.RenterUserId, o.Status });
        });
    }
}
