using Microsoft.EntityFrameworkCore;
using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class AnalyticsServiceDbContext : DbContext
{
    public AnalyticsServiceDbContext(DbContextOptions<AnalyticsServiceDbContext> options) : base(options)
    {
    }

    public DbSet<PaymentFinancialEvent> PaymentFinancialEvents => Set<PaymentFinancialEvent>();
    public DbSet<RentalOrderFact> RentalOrders => Set<RentalOrderFact>();
    public DbSet<RentalItemFact> RentalItems => Set<RentalItemFact>();
    public DbSet<ProcessedRentalEvent> ProcessedRentalEvents => Set<ProcessedRentalEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var financialEvent = modelBuilder.Entity<PaymentFinancialEvent>();
        financialEvent.ToTable("payment_financial_events");
        financialEvent.HasKey(x => x.MessageId);
        financialEvent.Property(x => x.MessageType).HasMaxLength(100).IsRequired();
        financialEvent.Property(x => x.LogicalEventId).HasMaxLength(200).IsRequired();
        financialEvent.Property(x => x.AggregateId).HasMaxLength(100).IsRequired();
        financialEvent.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        financialEvent.Property(x => x.InvoiceRef).HasMaxLength(100);
        financialEvent.Property(x => x.SettlementRef).HasMaxLength(100);
        financialEvent.Property(x => x.RefundRef).HasMaxLength(100);
        financialEvent.Property(x => x.PayoutRef).HasMaxLength(100);
        financialEvent.Property(x => x.RefundKind).HasMaxLength(50);

        foreach (var property in new[]
        {
            nameof(PaymentFinancialEvent.RentalAmount),
            nameof(PaymentFinancialEvent.DepositAmount),
            nameof(PaymentFinancialEvent.TotalAmount),
            nameof(PaymentFinancialEvent.FinalDeductionAmount),
            nameof(PaymentFinancialEvent.DepositRefundDue),
            nameof(PaymentFinancialEvent.PlatformFeeAmount),
            nameof(PaymentFinancialEvent.SupplierPayable),
            nameof(PaymentFinancialEvent.Amount)
        })
        {
            financialEvent.Property<decimal?>(property).HasPrecision(18, 2);
        }

        financialEvent.Property(x => x.PlatformFeeRate).HasPrecision(9, 6);
        financialEvent.HasIndex(x => x.LogicalEventId).IsUnique();
        financialEvent.HasIndex(x => new { x.MessageType, x.OccurredAt });
        financialEvent.HasIndex(x => new { x.MessageType, x.CompletedAt, x.Currency });
        financialEvent.HasIndex(x => new { x.RentalOrderId, x.MessageType });
        financialEvent.HasIndex(x => new { x.SupplierId, x.MessageType, x.CompletedAt });
        financialEvent.HasIndex(x => new { x.MessageType, x.RefundRef });

        var orders = modelBuilder.Entity<RentalOrderFact>();
        orders.ToTable("analytics_rental_orders");
        orders.HasKey(x => x.RentalOrderId);
        orders.Property(x => x.Status).HasMaxLength(40);
        orders.HasIndex(x => x.CreatedAt);
        orders.HasIndex(x => x.PaidAt);
        orders.HasIndex(x => x.CompletedAt);
        orders.HasIndex(x => x.CancelledAt);
        orders.HasIndex(x => x.ExpiredAt);
        orders.HasIndex(x => x.RejectedAt);
        orders.HasIndex(x => new { x.Status, x.PaidAt });

        var items = modelBuilder.Entity<RentalItemFact>();
        items.ToTable("analytics_rental_items");
        items.HasKey(x => x.RentalItemId);
        items.Property(x => x.EquipmentTypeName).HasMaxLength(200);
        items.Property(x => x.BrandName).HasMaxLength(200);
        items.Property(x => x.Model).HasMaxLength(200);
        items.Property(x => x.ExpiryReason).HasMaxLength(100);
        items.Property(x => x.RejectionReason).HasMaxLength(100);
        items.Property(x => x.RentalAmount).HasPrecision(18, 0);
        items.Property(x => x.DepositAmount).HasPrecision(18, 0);
        items.HasIndex(x => x.RentalOrderId);
        items.HasIndex(x => new { x.SupplierId, x.RentalOrderId });
        items.HasIndex(x => new { x.SupplierId, x.ConfirmedAt });
        items.HasIndex(x => new { x.SupplierId, x.RejectedAt });
        items.HasIndex(x => new { x.SupplierId, x.ExpiredAt });
        items.HasIndex(x => new { x.SupplierId, x.CompletedAt });
        items.HasIndex(x => x.EquipmentTypeId);
        items.HasIndex(x => x.EquipmentListingId);
        items.HasIndex(x => x.BrandId);

        var processed = modelBuilder.Entity<ProcessedRentalEvent>();
        processed.ToTable("analytics_processed_rental_events");
        processed.HasKey(x => x.MessageId);
    }
}
