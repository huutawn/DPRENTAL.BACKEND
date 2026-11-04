using Dprental.NotificationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.NotificationService.Domain.Repositories.Configurations;

public sealed class NotificationInboxReceiptConfiguration : IEntityTypeConfiguration<NotificationInboxReceipt>
{
    public void Configure(EntityTypeBuilder<NotificationInboxReceipt> builder)
    {
        builder.ToTable("inbox_receipts", "notification");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.ConsumerName).HasColumnName("consumer_name").HasMaxLength(64).IsRequired();
        builder.Property(r => r.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(r => r.Producer).HasColumnName("producer").HasMaxLength(64).IsRequired();
        builder.Property(r => r.MessageType).HasColumnName("message_type").HasMaxLength(64).IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        builder.Property(r => r.ProcessedAt).HasColumnName("processed_at").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(r => r.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        builder.Property(r => r.ErrorDetail).HasColumnName("error_detail").HasColumnType("text");

        builder.HasIndex(r => new { r.ConsumerName, r.MessageId })
            .IsUnique()
            .HasDatabaseName("uq_inbox_receipts_consumer_message");

        builder.HasIndex(r => r.ProcessedAt)
            .IsDescending()
            .HasDatabaseName("ix_inbox_receipts_processed_at");
    }
}
