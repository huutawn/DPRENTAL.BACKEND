using Dprental.PaymentService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class PaymentServiceDbContext : DbContext
{
    private readonly Dprental.PaymentService.Common.Cryptography.IPaymentDataEncryptor? _encryptor;

    public PaymentServiceDbContext(
        DbContextOptions<PaymentServiceDbContext> options,
        Dprental.PaymentService.Common.Cryptography.IPaymentDataEncryptor? encryptor = null) : base(options)
    {
        _encryptor = encryptor;
    }
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<OperationReceipt> OperationReceipts => Set<OperationReceipt>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<PaymentTransactionResolution> PaymentTransactionResolutions => Set<PaymentTransactionResolution>();
    public DbSet<RentalItemSettlementRecord> RentalItemSettlements => Set<RentalItemSettlementRecord>();
    public DbSet<SettlementLedgerEntry> SettlementLedgerEntries => Set<SettlementLedgerEntry>();
    public DbSet<ManualRefundRequirement> ManualRefundRequirements => Set<ManualRefundRequirement>();
    public DbSet<ManualRefundOperationAudit> ManualRefundOperationAudits => Set<ManualRefundOperationAudit>();
    public DbSet<SupplierPayout> SupplierPayouts => Set<SupplierPayout>();
    public DbSet<FinancialOperationAudit> FinancialOperationAudits => Set<FinancialOperationAudit>();
    public DbSet<TransactionJournalEntry> TransactionJournals => Set<TransactionJournalEntry>();
    public DbSet<OperationHeader> OperationHeaders => Set<OperationHeader>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentServiceDbContext).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EncryptSensitiveEntities();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        EncryptSensitiveEntities();
        return base.SaveChanges();
    }

    private void EncryptSensitiveEntities()
    {
        if (_encryptor is null) return;

        foreach (var entry in ChangeTracker.Entries<PaymentTransaction>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                var pt = entry.Entity;
                if (!Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.IsEncrypted(pt.AccountNumber))
                {
                    string idStr = pt.Id.ToString("D");
                    string encAccount = Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Encrypt(
                        _encryptor, "PaymentTransaction", idStr, "AccountNumber", pt.AccountNumber);
                    string? encSub = !string.IsNullOrEmpty(pt.SubAccount) && !Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.IsEncrypted(pt.SubAccount)
                        ? Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Encrypt(_encryptor, "PaymentTransaction", idStr, "SubAccount", pt.SubAccount)
                        : pt.SubAccount;
                    string? encContent = !string.IsNullOrEmpty(pt.Content) && !Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.IsEncrypted(pt.Content)
                        ? Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Encrypt(_encryptor, "PaymentTransaction", idStr, "Content", pt.Content)
                        : pt.Content;

                    entry.Property(t => t.AccountNumber).CurrentValue = encAccount;
                    entry.Property(t => t.SubAccount).CurrentValue = encSub;
                    entry.Property(t => t.Content).CurrentValue = encContent;
                }
            }
        }

        foreach (var entry in ChangeTracker.Entries<SupplierPayout>())
            EncryptRecipientAccount(entry, nameof(SupplierPayout), entry.Entity.Id);
        foreach (var entry in ChangeTracker.Entries<ManualRefundRequirement>())
            EncryptRecipientAccount(entry, nameof(ManualRefundRequirement), entry.Entity.Id);
    }

    // Đọc lại qua TransferQrBuilder, dùng cùng entityType/recordId/fieldName làm AAD.
    private void EncryptRecipientAccount<T>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T> entry, string entityType, Guid id)
        where T : class
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified)) return;
        var property = entry.Property<string?>("RecipientAccountNumber");
        string? plain = property.CurrentValue;
        if (string.IsNullOrEmpty(plain) || Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.IsEncrypted(plain)) return;
        property.CurrentValue = Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Encrypt(
            _encryptor!, entityType, id.ToString("D"), "RecipientAccountNumber", plain);
    }
}
