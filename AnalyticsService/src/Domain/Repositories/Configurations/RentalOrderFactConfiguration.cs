using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng rental_order_facts: khóa chính, kiểu cột và index phục vụ báo cáo theo ngày và trạng thái.
/// </summary>
public sealed class RentalOrderFactConfiguration : IEntityTypeConfiguration<RentalOrderFact>
{
    public void Configure(EntityTypeBuilder<RentalOrderFact> builder)
    {
        builder.ToTable("rental_order_facts");                 // tên bảng theo ticket
        builder.HasKey(x => x.OrderId);                        // khóa chính
        builder.Property(x => x.OrderId).ValueGeneratedNever(); // Id lấy từ event, DB không tự sinh

        builder.Property(x => x.OrderCode).HasMaxLength(64);
        builder.Property(x => x.Status)
            .HasConversion<string>()                           // lưu "Completed" thay vì 2
            .HasMaxLength(32);
        builder.Property(x => x.RowVersion).IsRowVersion();    // Npgsql gắn uint vào cột xmin

        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => x.PaidAt);
        builder.HasIndex(x => x.CompletedAt);
        builder.HasIndex(x => x.Status);
    }
}