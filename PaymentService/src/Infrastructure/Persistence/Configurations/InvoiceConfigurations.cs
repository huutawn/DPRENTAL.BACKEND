using Dprental.PaymentService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.InvoiceRef).IsRequired().HasMaxLength(64);
        builder.Property(i => i.RentalOrderId).IsRequired();
        builder.Property(i => i.OrderCode).IsRequired().HasMaxLength(64);
        builder.Property(i => i.RenterId).IsRequired();
        builder.Property(i => i.Currency).IsRequired().HasMaxLength(3);
        builder.Property(i => i.TotalRentalAmount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(i => i.TotalDepositAmount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(i => i.TotalAmount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(i => i.Status).IsRequired().HasMaxLength(32);
        builder.Property(i => i.PaymentCode).IsRequired().HasMaxLength(32);
        builder.Property(i => i.MerchantBank).IsRequired().HasMaxLength(64);
        builder.Property(i => i.MerchantAccount).IsRequired().HasMaxLength(64);
        builder.Property(i => i.PaymentDeadline).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.Property(i => i.PaymentRef).HasMaxLength(64);
        builder.Property(i => i.CancelReason).HasMaxLength(256);

        builder.HasIndex(i => i.InvoiceRef).IsUnique();
        builder.HasIndex(i => i.RentalOrderId).IsUnique();
        builder.HasIndex(i => i.PaymentCode).IsUnique();
        builder.HasIndex(i => i.PaymentRef).IsUnique();

        builder.Navigation(i => i.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class InvoiceItemConfiguration : IEntityTypeConfiguration<InvoiceItem>
{
    public void Configure(EntityTypeBuilder<InvoiceItem> builder)
    {
        builder.ToTable("invoice_items");

        builder.HasKey(ii => ii.Id);

        builder.Property(ii => ii.InvoiceId).IsRequired();
        builder.Property(ii => ii.RentalItemId).IsRequired();
        builder.Property(ii => ii.SupplierId).IsRequired();
        builder.Property(ii => ii.RentalAmount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(ii => ii.DepositAmount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(ii => ii.TotalAmount).IsRequired().HasColumnType("numeric(18,0)");

        builder.HasOne(ii => ii.Invoice)
            .WithMany(i => i.Items)
            .HasForeignKey(ii => ii.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ii => new { ii.InvoiceId, ii.RentalItemId }).IsUnique();
    }
}
