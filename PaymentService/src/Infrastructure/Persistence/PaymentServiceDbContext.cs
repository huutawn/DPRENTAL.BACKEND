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
    public DbSet<ManualRefundRequirement> ManualRefundRequirements => Set<ManualRefundRequirement>();
    public DbSet<TransactionJournalEntry> TransactionJournals => Set<TransactionJournalEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentServiceDbContext).Assembly);
    }
}
