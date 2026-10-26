using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng analytics_dead_letters: mỗi vị trí message lỗi chỉ được ghi một lần.
/// </summary>
public sealed class AnalyticsDeadLetterConfiguration : IEntityTypeConfiguration<AnalyticsDeadLetter>
{
    public void Configure(EntityTypeBuilder<AnalyticsDeadLetter> builder)
    {
        builder.ToTable("analytics_dead_letters");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Analytics tự tạo Guid trước khi lưu

        builder.Property(x => x.Topic).HasMaxLength(255);
        builder.Property(x => x.MessageType).HasMaxLength(128);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);

        // Index duy nhất: worker retry cùng message lỗi không tạo thêm dòng thứ hai
        builder.HasIndex(x => new { x.Topic, x.Partition, x.Offset }).IsUnique();
    }
}
