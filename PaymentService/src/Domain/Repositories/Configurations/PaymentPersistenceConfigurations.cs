using Dprental.PaymentService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dprental.PaymentService.Domain.Repositories.Configurations;

public sealed class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("payment_transactions");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Provider).IsRequired().HasMaxLength(32);
        builder.Property(t => t.ProviderTransactionId).IsRequired().HasMaxLength(64);
        builder.Property(t => t.PaymentRef).IsRequired().HasMaxLength(64);
        builder.Property(t => t.Gateway).IsRequired().HasMaxLength(64);
        builder.Property(t => t.AccountNumber).IsRequired().HasMaxLength(512);
        builder.Property(t => t.SubAccount).HasMaxLength(512);
        builder.Property(t => t.TransferType).IsRequired().HasMaxLength(16);
        builder.Property(t => t.TransferAmount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(t => t.Accumulated).HasColumnType("numeric(18,0)");
        builder.Property(t => t.TransactionDate).IsRequired();
        builder.Property(t => t.ReferenceCode).HasMaxLength(128);
        builder.Property(t => t.Description).HasMaxLength(1000);
        builder.Property(t => t.Content).HasMaxLength(1000);
        builder.Property(t => t.ProviderCode).HasColumnType("text");
        builder.Property(t => t.PaymentCode).HasMaxLength(32);
        builder.Property(t => t.InvoiceRef).HasMaxLength(64);
        builder.Property(t => t.Status).IsRequired().HasMaxLength(32);
        builder.Property(t => t.ReasonCode).IsRequired().HasMaxLength(64);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.Revision).IsRequired().HasDefaultValue(1);

        builder.HasIndex(t => new { t.Provider, t.ProviderTransactionId }).IsUnique();
        builder.HasIndex(t => t.PaymentRef);
        builder.HasIndex(t => t.InvoiceRef);
    }
}

public sealed class OperationReceiptConfiguration : IEntityTypeConfiguration<OperationReceipt>
{
    public void Configure(EntityTypeBuilder<OperationReceipt> builder)
    {
        builder.ToTable("operation_receipts");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.OperationKey).IsRequired().HasMaxLength(64);
        builder.Property(r => r.OperationType).IsRequired().HasMaxLength(32);
        builder.Property(r => r.PayloadFingerprint).IsRequired().HasMaxLength(64);
        builder.Property(r => r.ResultJson).IsRequired();
        builder.Property(r => r.InvoiceRef).IsRequired().HasMaxLength(64);
        builder.Property(r => r.RentalOrderId).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.Revision).IsRequired().HasDefaultValue(1);

        builder.HasIndex(r => r.OperationKey).IsUnique();
        builder.HasIndex(r => r.RentalOrderId);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.MessageId).IsRequired();
        builder.Property(o => o.MessageType).IsRequired().HasMaxLength(64);
        builder.Property(o => o.AggregateType).IsRequired().HasMaxLength(64);
        builder.Property(o => o.AggregateId).IsRequired().HasMaxLength(64);
        builder.Property(o => o.Payload).IsRequired();
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.LockedBy).HasMaxLength(128);
        builder.Property(o => o.LastErrorMessage).HasMaxLength(2000);
        builder.Property(o => o.EventId);
        builder.Property(o => o.OperationId);
        builder.Property(o => o.Revision).IsRequired().HasDefaultValue(1);
        builder.Property(o => o.QuarantinedAt);
        builder.Property(o => o.QuarantineReason).HasMaxLength(512);

        builder.HasIndex(o => o.PublishedAt);
        builder.HasIndex(o => o.QuarantinedAt);
        builder.HasIndex(o => new { o.PublishedAt, o.LockedUntilUtc, o.NextRetryAt, o.RetryCount });
    }
}

public sealed class OperationHeaderConfiguration : IEntityTypeConfiguration<OperationHeader>
{
    public void Configure(EntityTypeBuilder<OperationHeader> builder)
    {
        builder.ToTable("operation_headers");

        builder.HasKey(o => o.OperationId);

        builder.Property(o => o.OperationType).IsRequired().HasMaxLength(64);
        builder.Property(o => o.MemberCount).IsRequired();
        builder.Property(o => o.MemberDigest).IsRequired().HasMaxLength(128);
        builder.Property(o => o.EntityClaimsDigest).IsRequired().HasMaxLength(128);
        builder.Property(o => o.Actor).IsRequired().HasMaxLength(128);
        builder.Property(o => o.CreatedAt).IsRequired();

        builder.HasIndex(o => o.CreatedAt);
    }
}

public sealed class RentalItemSettlementRecordConfiguration : IEntityTypeConfiguration<RentalItemSettlementRecord>
{
    public void Configure(EntityTypeBuilder<RentalItemSettlementRecord> builder)
    {
        builder.ToTable("rental_item_settlements", t =>
        {
            t.HasCheckConstraint("ck_settlement_amounts_non_negative",
                "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0 AND \"FinalDeductionAmount\" >= 0 " +
                "AND \"DepositRefundDue\" >= 0 AND \"PlatformFeeAmount\" >= 0 AND \"SupplierPayable\" >= 0");
            t.HasCheckConstraint("ck_settlement_deduction_le_deposit",
                "\"FinalDeductionAmount\" <= \"DepositAmount\"");
            t.HasCheckConstraint("ck_settlement_fee_rate_range",
                "\"PlatformFeeRate\" >= 0 AND \"PlatformFeeRate\" <= 1");
            t.HasCheckConstraint("ck_settlement_money_balance",
                "\"SupplierPayable\" + \"PlatformFeeAmount\" + \"DepositRefundDue\" = \"RentalAmount\" + \"DepositAmount\"");
            t.HasCheckConstraint("ck_settlement_status_valid", "\"Status\" IN (1, 2)");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SettlementRef).IsRequired().HasMaxLength(64);
        builder.Property(x => x.OperationKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.InvoiceId).IsRequired();
        builder.Property(x => x.InvoiceItemId).IsRequired();
        builder.Property(x => x.RentalItemId).IsRequired();
        builder.Property(x => x.SupplierId).IsRequired();
        builder.Property(x => x.DecisionRef).IsRequired().HasMaxLength(256);

        builder.Property(x => x.RentalAmount).HasColumnType("numeric(18,0)");
        builder.Property(x => x.DepositAmount).HasColumnType("numeric(18,0)");
        builder.Property(x => x.FinalDeductionAmount).HasColumnType("numeric(18,0)");
        builder.Property(x => x.DepositRefundDue).HasColumnType("numeric(18,0)");
        builder.Property(x => x.PlatformFeeRate).HasColumnType("numeric(5,4)");
        builder.Property(x => x.PlatformFeeAmount).HasColumnType("numeric(18,0)");
        builder.Property(x => x.SupplierPayable).HasColumnType("numeric(18,0)");

        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Revision).IsRequired().HasDefaultValue(1);

        builder.HasIndex(x => x.OperationKey).IsUnique();
        builder.HasIndex(x => x.RentalItemId).IsUnique();
        builder.HasIndex(x => x.SettlementRef).IsUnique();

        builder.HasMany(x => x.Entries).WithOne(x => x.Settlement)
            .HasForeignKey(x => x.SettlementId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.RefundRequirement).WithOne(x => x.Settlement)
            .HasForeignKey<ManualRefundRequirement>(x => x.SettlementId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<InvoiceItem>().WithMany()
            .HasForeignKey(x => x.InvoiceItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SettlementLedgerEntryConfiguration : IEntityTypeConfiguration<SettlementLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SettlementLedgerEntry> builder)
    {
        builder.ToTable("settlement_ledger_entries", t =>
        {
            t.HasCheckConstraint("ck_settlement_entry_amount_non_negative", "\"Amount\" >= 0");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SettlementId).IsRequired();
        builder.Property(x => x.EntryType).HasConversion<int>().IsRequired();
        builder.Property(x => x.Amount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Revision).IsRequired().HasDefaultValue(1);

        builder.HasIndex(x => new { x.SettlementId, x.EntryType }).IsUnique();
    }
}

public sealed class ManualRefundRequirementConfiguration : IEntityTypeConfiguration<ManualRefundRequirement>
{
    public void Configure(EntityTypeBuilder<ManualRefundRequirement> builder)
    {
        builder.ToTable("manual_refund_requirements", t =>
        {
            t.HasCheckConstraint("ck_manual_refund_amount_positive", "\"Amount\" > 0");
            t.HasCheckConstraint("ck_manual_refund_status_valid", "\"Status\" IN (1, 2)");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.RequirementRef).IsRequired().HasMaxLength(64);
        builder.Property(x => x.SettlementId).IsRequired();
        builder.Property(x => x.InvoiceId).IsRequired();
        builder.Property(x => x.RentalItemId).IsRequired();
        builder.Property(x => x.RenterId).IsRequired();
        builder.Property(x => x.PaymentRef).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Amount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Revision).IsRequired().HasDefaultValue(1);
        builder.Property(x => x.ExternalTransferReference).HasMaxLength(256);
        builder.HasIndex(x => x.RentalItemId).IsUnique();
        builder.HasIndex(x => x.RequirementRef).IsUnique();
    }
}

public sealed class TransactionJournalEntryConfiguration : IEntityTypeConfiguration<TransactionJournalEntry>
{
    public void Configure(EntityTypeBuilder<TransactionJournalEntry> builder)
    {
        builder.ToTable("transaction_journal");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.OperationId).IsRequired();
        builder.Property(j => j.StreamId).IsRequired().HasMaxLength(128);
        builder.Property(j => j.StreamType).IsRequired().HasMaxLength(64);
        builder.Property(j => j.Version).IsRequired();
        builder.Property(j => j.EventId).IsRequired();
        builder.Property(j => j.EventType).IsRequired().HasMaxLength(64);
        builder.Property(j => j.Amount).IsRequired().HasColumnType("numeric(18,0)");
        builder.Property(j => j.Currency).IsRequired().HasMaxLength(10);
        builder.Property(j => j.Status).IsRequired().HasMaxLength(64);
        builder.Property(j => j.ConfirmationSource).IsRequired().HasMaxLength(64);
        builder.Property(j => j.Actor).IsRequired().HasMaxLength(128);
        builder.Property(j => j.TimestampUtc).IsRequired();

        builder.Property(j => j.PaymentRef).HasMaxLength(128);
        builder.Property(j => j.InvoiceRef).HasMaxLength(64);
        builder.Property(j => j.OutboxDigest).HasMaxLength(128);
        builder.Property(j => j.OutboxMessageId);
        builder.Property(j => j.EnvelopeDigest).IsRequired().HasMaxLength(128);
        builder.Property(j => j.PreviousHash).IsRequired().HasMaxLength(128);
        builder.Property(j => j.PayloadHash).IsRequired().HasMaxLength(128);
        builder.Property(j => j.EntryHash).IsRequired().HasMaxLength(128);
        builder.Property(j => j.Fingerprint).IsRequired().HasMaxLength(128);
        builder.Property(j => j.EncryptedPayload).IsRequired().HasColumnType("text");
        builder.Property(j => j.PayloadNonce).IsRequired().HasMaxLength(64);
        builder.Property(j => j.PayloadTag).IsRequired().HasMaxLength(64);
        builder.Property(j => j.EncryptionKeyId).IsRequired().HasMaxLength(64);

        builder.Property(j => j.ProofSignature).HasMaxLength(512);
        builder.Property(j => j.SigningKeyId).HasMaxLength(64);

        builder.HasIndex(j => new { j.StreamId, j.Version }).IsUnique();
        builder.HasIndex(j => j.EventId).IsUnique();
        builder.HasIndex(j => j.StreamId);
        builder.HasIndex(j => j.PaymentRef);
        builder.HasIndex(j => j.InvoiceRef);
        builder.HasIndex(j => j.RentalOrderId);
        builder.HasIndex(j => j.OperationId);
        builder.HasIndex(j => j.OutboxDigest);
        builder.HasIndex(j => new { j.IsConfirmed, j.TimestampUtc });
        builder.HasIndex(j => new { j.TimestampUtc, j.Id });
    }
}
