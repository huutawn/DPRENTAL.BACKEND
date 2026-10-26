using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.AnalyticsService.Domain.Repositories.Configurations;

/// <summary>
/// Cấu hình bảng analytics_inbox_messages. Khóa chính MessageId đã là index nên không cần thêm index.
/// </summary>
public sealed class AnalyticsInboxMessageConfiguration : IEntityTypeConfiguration<AnalyticsInboxMessage>
{
    public void Configure(EntityTypeBuilder<AnalyticsInboxMessage> builder)
    {
        builder.ToTable("analytics_inbox_messages");
        builder.HasKey(x => x.MessageId);
        builder.Property(x => x.MessageId).ValueGeneratedNever(); // Lấy từ envelope, DB không tự sinh

        builder.Property(x => x.MessageType).HasMaxLength(128);
        builder.Property(x => x.Topic).HasMaxLength(255); // Kafka giới hạn tên topic 249 ký tự
    }
}
