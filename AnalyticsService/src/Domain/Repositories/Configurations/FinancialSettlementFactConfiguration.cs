using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng financial_settlement_facts. Dòng chỉ insert một lần nên không cần chống ghi đè đồng thời.
/// </summary>
public sealed class FinancialSettlementFactConfiguration : IEntityTypeConfiguration<FinancialSettlementFact>
{
    public void Configure(EntityTypeBuilder<FinancialSettlementFact> builder)
    {
        builder.ToTable("financial_settlement_facts");
        builder.HasKey(x => x.SettlementRef); // Mã Payment gửi; trùng mã → cùng một lần quyết toán
        builder.Property(x => x.SettlementRef).HasMaxLength(64);

        builder.HasIndex(x => x.CompletedAt);                     // PlatformRevenue, DeductionAmount theo ngày
        builder.HasIndex(x => new { x.SupplierId, x.CompletedAt }); // SupplierPayable của một NCC theo ngày
        builder.HasIndex(x => x.RentalItemId);                    // Nối với rental_item_facts
    }
}
