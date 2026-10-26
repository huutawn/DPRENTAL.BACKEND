using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng rental_item_facts: index ghép (nhóm + ngày) phục vụ báo cáo theo NCC, loại máy, hãng, listing.
/// </summary>
public sealed class RentalItemFactConfiguration : IEntityTypeConfiguration<RentalItemFact>
{
    public void Configure(EntityTypeBuilder<RentalItemFact> builder)
    {
        builder.ToTable("rental_item_facts");
        builder.HasKey(x => x.RentalItemId);
        builder.Property(x => x.RentalItemId).ValueGeneratedNever(); // Id lấy từ event, DB không tự sinh

        builder.Property(x => x.EquipmentTypeName).HasMaxLength(200);
        builder.Property(x => x.BrandName).HasMaxLength(200);
        builder.Property(x => x.Model).HasMaxLength(200);
        builder.Property(x => x.ExpiryReason).HasMaxLength(64);
        builder.Property(x => x.Status)
            .HasConversion<string>() // lưu "InRental" thay vì 3
            .HasMaxLength(32);
        builder.Property(x => x.RowVersion).IsRowVersion(); // Npgsql gắn uint vào cột xmin

        // Index ghép: cột lọc nhóm đứng trước, cột ngày đứng sau.
        // Dashboard luôn hỏi "NCC/loại máy/hãng/listing NÀY, trong KHOẢNG NGÀY này".
        builder.HasIndex(x => new { x.SupplierId, x.PaidAt });
        builder.HasIndex(x => new { x.EquipmentTypeId, x.PaidAt });
        builder.HasIndex(x => new { x.BrandId, x.PaidAt });
        builder.HasIndex(x => new { x.EquipmentListingId, x.PaidAt });

        builder.HasIndex(x => x.HandoverAt);  // RentedQuantity theo ngày bàn giao
        builder.HasIndex(x => x.CompletedAt); // CompletedRentalValue theo ngày hoàn thành
        builder.HasIndex(x => x.OrderId);     // Tìm mọi item của một đơn
        builder.HasIndex(x => x.Status);
    }
}
