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
    public DbSet<RentalItemSettlementRecord> RentalItemSettlements => Set<RentalItemSettlementRecord>();
    public DbSet<SettlementLedgerEntry> SettlementLedgerEntries => Set<SettlementLedgerEntry>();


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

        modelBuilder.Entity<OutboxMessage>()
            .HasIndex(o => o.PublishedAt);

        modelBuilder.Entity<PaymentTransaction>(b =>
        {
            b.HasIndex(t => new { t.Provider, t.ProviderTransactionId }).IsUnique();
            b.HasIndex(t => t.PaymentRef);
            b.HasIndex(t => t.InvoiceRef);
        });
        modelBuilder.Entity<RentalItemSettlementRecord>(b =>
        {
            b.HasIndex(x => x.OperationKey).IsUnique();
            b.HasIndex(x => x.RentalItemId).IsUnique();
            b.HasIndex(x => x.SettlementRef).IsUnique();

            b.HasMany(x => x.Entries).WithOne(x => x.Settlement)
                .HasForeignKey(x => x.SettlementId).OnDelete(DeleteBehavior.Restrict);

            b.HasOne<InvoiceItem>().WithMany()
                .HasForeignKey(x => x.InvoiceItemId).OnDelete(DeleteBehavior.Restrict);

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
            });
        });

        modelBuilder.Entity<SettlementLedgerEntry>(b =>
        {
            b.HasIndex(x => new { x.SettlementId, x.EntryType }).IsUnique();
            b.Property(x => x.EntryType).HasConversion<int>();
            b.ToTable(t => t.HasCheckConstraint("ck_settlement_entry_amount_non_negative", "\"Amount\" >= 0"));
        });
    }
}
