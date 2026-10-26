using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng supplier_payout_facts.
/// </summary>
public sealed class SupplierPayoutFactConfiguration : IEntityTypeConfiguration<SupplierPayoutFact>
{
    public void Configure(EntityTypeBuilder<SupplierPayoutFact> builder)
    {
        builder.ToTable("supplier_payout_facts");
        builder.HasKey(x => x.PayoutRef);
        builder.Property(x => x.PayoutRef).HasMaxLength(64);
        builder.Property(x => x.Status).HasMaxLength(32); // Giữ nguyên chữ Payment gửi, không đổi sang enum

        builder.HasIndex(x => x.CompletedAt);                     // SupplierPayoutCompleted theo ngày
        builder.HasIndex(x => new { x.SupplierId, x.CompletedAt }); // Payout của một NCC theo ngày
        builder.HasIndex(x => x.Status);
    }
}
