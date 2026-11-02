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

        builder.Property(r => r.ConsumerName).HasMaxLength(64).IsRequired();
        builder.Property(r => r.MessageId).IsRequired();
        builder.Property(r => r.Producer).HasMaxLength(64).IsRequired();
        builder.Property(r => r.MessageType).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(32).IsRequired();
        builder.Property(r => r.ProcessedAt).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.ErrorDetail).HasColumnType("text");

        builder.HasIndex(r => new { r.ConsumerName, r.MessageId })
            .IsUnique()
            .HasDatabaseName("uq_inbox_receipts_consumer_message");

        builder.HasIndex(r => r.ProcessedAt)
            .IsDescending()
            .HasDatabaseName("ix_inbox_receipts_processed_at");
    }
}
