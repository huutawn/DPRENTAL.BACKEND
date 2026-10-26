using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng refund_facts: khóa chính gồm 2 cột vì một lần hoàn tiền có thể gồm nhiều dòng thuê.
/// </summary>
public sealed class RefundFactConfiguration : IEntityTypeConfiguration<RefundFact>
{
    public void Configure(EntityTypeBuilder<RefundFact> builder)
    {
        builder.ToTable("refund_facts");
        builder.HasKey(x => new { x.RefundRef, x.RentalItemId }); // Khóa chính 2 cột
        builder.Property(x => x.RefundRef).HasMaxLength(64);
        builder.Property(x => x.Kind).HasMaxLength(64);

        builder.HasIndex(x => x.CompletedAt);  // RefundAmount theo ngày hoàn tiền
        builder.HasIndex(x => x.OrderId);      // Tra cứu theo đơn
        builder.HasIndex(x => x.RentalItemId); // Nối với rental_item_facts.PaidAt → GrossRentalValueRefunded
    }
}
