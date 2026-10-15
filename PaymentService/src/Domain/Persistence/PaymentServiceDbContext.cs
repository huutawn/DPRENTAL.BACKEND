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
    }
}
