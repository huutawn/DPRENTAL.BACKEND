using Dprental.PaymentService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Persistence;

public sealed class PaymentServiceDbContext : DbContext
{
    public PaymentServiceDbContext(DbContextOptions<PaymentServiceDbContext> options) : base(options)
    {
    }

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<OperationReceipt> OperationReceipts => Set<OperationReceipt>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<PaymentTransactionResolution> PaymentTransactionResolutions => Set<PaymentTransactionResolution>();
    public DbSet<RentalItemSettlementRecord> RentalItemSettlements => Set<RentalItemSettlementRecord>();
    public DbSet<SettlementLedgerEntry> SettlementLedgerEntries => Set<SettlementLedgerEntry>();
    public DbSet<ManualRefundRequirement> ManualRefundRequirements => Set<ManualRefundRequirement>();
    public DbSet<ManualRefundOperationAudit> ManualRefundOperationAudits => Set<ManualRefundOperationAudit>();
    public DbSet<SupplierPayout> SupplierPayouts => Set<SupplierPayout>();
    public DbSet<SupplierPayoutDestination> SupplierPayoutDestinations => Set<SupplierPayoutDestination>();
    public DbSet<FinancialOperationAudit> FinancialOperationAudits => Set<FinancialOperationAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasIndex(i => i.InvoiceRef).IsUnique();
            b.HasIndex(i => i.RentalOrderId).IsUnique();
            b.HasIndex(i => i.PaymentCode).IsUnique();
            b.HasIndex(i => i.PaymentRef).IsUnique();
            b.Navigation(i => i.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<InvoiceItem>()
            .HasIndex(ii => new { ii.InvoiceId, ii.RentalItemId })
            .IsUnique();

        modelBuilder.Entity<OperationReceipt>(b =>
        {
            b.HasIndex(r => r.OperationKey).IsUnique();
            b.HasIndex(r => r.RentalOrderId);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.HasIndex(o => o.PublishedAt);
            b.HasIndex(o => new { o.PublishedAt, o.LockedUntilUtc, o.NextRetryAt, o.RetryCount });
        });

        modelBuilder.Entity<PaymentTransaction>(b =>
        {
            b.HasIndex(t => new { t.Provider, t.ProviderTransactionId }).IsUnique();
            b.HasIndex(t => t.PaymentRef);
            b.HasIndex(t => t.InvoiceRef);
        });

        modelBuilder.Entity<PaymentTransactionResolution>(b =>
        {
            b.HasIndex(x => x.TransactionId).IsUnique();
            b.HasIndex(x => x.OperationKey).IsUnique();
            b.HasOne<PaymentTransaction>().WithMany()
                .HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany()
                .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RentalItemSettlementRecord>(b =>
        {
            b.HasIndex(x => x.OperationKey).IsUnique();
            b.HasIndex(x => x.RentalItemId).IsUnique();
            b.HasIndex(x => x.SettlementRef).IsUnique();
            b.Property(x => x.Status).HasConversion<int>();

            b.HasMany(x => x.Entries).WithOne(x => x.Settlement)
                .HasForeignKey(x => x.SettlementId).OnDelete(DeleteBehavior.Restrict);

            b.HasOne(x => x.RefundRequirement).WithOne(x => x.Settlement)
                .HasForeignKey<ManualRefundRequirement>(x => x.SettlementId).OnDelete(DeleteBehavior.Restrict);

            b.HasOne<InvoiceItem>().WithMany()
                .HasForeignKey(x => x.InvoiceItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_rental_item_settlements_Status_CreatedAt");
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_settlement_amounts_non_negative",
                    "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0 AND \"FinalDeductionAmount\" >= 0 " +
                    "AND \"DepositRefundDue\" >= 0 AND \"PlatformFeeAmount\" >= 0 AND \"SupplierPayable\" >= 0");
                t.HasCheckConstraint("ck_settlement_deduction_le_deposit",
                    "\"FinalDeductionAmount\" <= \"DepositAmount\"");
                t.HasCheckConstraint("ck_settlement_fee_rate_range",
                    "\"PlatformFeeRate\" >= 0 AND \"PlatformFeeRate\" <= 1");
                t.HasCheckConstraint("ck_settlement_money_balance",
                    "\"SupplierPayable\" + \"PlatformFeeAmount\" + \"DepositRefundDue\" = \"RentalAmount\" + \"DepositAmount\"");
                t.HasCheckConstraint("ck_settlement_status_valid", "\"Status\" IN (1, 2)");
            });
        });

        modelBuilder.Entity<SettlementLedgerEntry>(b =>
        {
            b.HasIndex(x => new { x.SettlementId, x.EntryType }).IsUnique();
            b.Property(x => x.EntryType).HasConversion<int>();
            b.ToTable(t => t.HasCheckConstraint("ck_settlement_entry_amount_non_negative", "\"Amount\" >= 0"));
        });

        modelBuilder.Entity<ManualRefundRequirement>(b =>
        {
            b.HasIndex(x => x.SettlementId).IsUnique();
            b.HasIndex(x => x.RentalItemId).IsUnique();
            b.HasIndex(x => x.RequirementRef).IsUnique();
            b.HasIndex(x => x.ExternalTransferReference).IsUnique()
                .HasDatabaseName("UX_manual_refund_requirements_ExternalTransferReference");
            b.Property(x => x.Status).HasConversion<int>();
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_manual_refund_requirements_Status_CreatedAt");
            b.ToTable(t =>
            {
                t.HasCheckConstraint("ck_manual_refund_amount_positive", "\"Amount\" > 0");
                t.HasCheckConstraint("ck_manual_refund_status_valid", "\"Status\" IN (1, 2)");
            });
        });

        modelBuilder.Entity<ManualRefundOperationAudit>(b =>
        {
            b.HasIndex(x => x.OperationKey).IsUnique();
            b.HasIndex(x => new { x.RefundRequirementId, x.OccurredAt });
            b.HasOne<ManualRefundRequirement>().WithMany()
                .HasForeignKey(x => x.RefundRequirementId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierPayout>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_supplier_payouts_amount_positive", "\"Amount\" > 0");
                t.HasCheckConstraint("CK_supplier_payouts_status_valid", "\"Status\" IN ('PENDING_PAYOUT', 'COMPLETED')");
                t.HasCheckConstraint("CK_supplier_payouts_completion_consistent",
                    "(\"Status\" = 'PENDING_PAYOUT' AND \"CompletedAt\" IS NULL AND \"CompletedBy\" IS NULL AND \"ExternalTransferReference\" IS NULL) OR " +
                    "(\"Status\" = 'COMPLETED' AND \"CompletedAt\" IS NOT NULL AND \"CompletedBy\" IS NOT NULL AND \"ExternalTransferReference\" IS NOT NULL AND \"DestinationRef\" IS NOT NULL)");
            });

            e.HasIndex(x => x.SettlementId).IsUnique().HasDatabaseName("UX_supplier_payouts_SettlementId");
            e.HasIndex(x => x.PayoutRef).IsUnique().HasDatabaseName("UX_supplier_payouts_PayoutRef");
            e.HasIndex(x => x.ExternalTransferReference).IsUnique()
                .HasDatabaseName("UX_supplier_payouts_ExternalTransferReference");
            e.HasIndex(x => new { x.RentalItemId, x.SupplierId }).IsUnique()
                .HasDatabaseName("UX_supplier_payouts_RentalItemId_SupplierId");
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_supplier_payouts_Status_CreatedAt");
            e.HasIndex(x => new { x.SupplierId, x.CreatedAt }).HasDatabaseName("IX_supplier_payouts_SupplierId_CreatedAt");
            e.HasOne(x => x.Settlement)
                .WithOne(x => x.Payout)
                .HasForeignKey<SupplierPayout>(x => x.SettlementId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Destination).WithMany()
                .HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierPayoutDestination>(e =>
        {
            e.HasIndex(x => new { x.DestinationRef, x.Version }).IsUnique();
            e.HasIndex(x => x.OperationKey).IsUnique();
            e.HasIndex(x => new { x.SupplierId, x.AccountFingerprint });
        });

        modelBuilder.Entity<FinancialOperationAudit>(e =>
        {
            e.HasIndex(x => x.OperationKey).IsUnique();
            e.HasIndex(x => new { x.PayoutId, x.OccurredAt });
            e.HasOne<SupplierPayout>().WithMany()
                .HasForeignKey(x => x.PayoutId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
