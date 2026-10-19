using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

/// <summary>
/// Repository quản lý persistence nguyên tử của hóa đơn, receipts, outbox messages và journal entries bắt buộc.
/// </summary>
public interface IInvoiceRepository
{
    Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task<OperationReceipt?> GetOperationReceiptAsync(string operationKey, CancellationToken cancellationToken = default);

    Task<bool> ExistsByRentalOrderIdAsync(Guid rentalOrderId, CancellationToken cancellationToken = default);

    Task<Invoice?> GetInvoiceSnapshotAsync(string invoiceRef, CancellationToken cancellationToken = default);

    Task<bool> IsPaymentRefDuplicateAsync(string paymentRef, string currentInvoiceRef, CancellationToken cancellationToken = default);
    Task<TransactionJournalEntry?> GetLatestJournalEntryAsync(string streamId, CancellationToken cancellationToken = default);


    Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default);
    Task<PaymentTransaction?> FindPaymentTransactionWithRowLockAsync(string provider, string providerTransactionId, CancellationToken cancellationToken = default);

    Task<Invoice?> FindInvoiceByPaymentCodeWithRowLockAsync(string paymentCode, CancellationToken cancellationToken = default);

    Task SavePaymentTransactionAsync(
        PaymentTransaction transaction,
        Invoice? invoice,
        OutboxMessage? outboxMessage,
        TransactionJournalEntry journalEntry,
        TransactionJournalEntry? invoiceJournalEntry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default);

    Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveNewInvoiceAsync(
        Invoice invoice,
        OperationReceipt receipt,
        OutboxMessage? outboxMessage,
        TransactionJournalEntry journalEntry,
        CancellationToken cancellationToken = default);

    Task SaveInvoiceChangesAsync(
        Invoice invoice,
        OperationReceipt? receipt,
        OutboxMessage? outboxMessage,
        TransactionJournalEntry journalEntry,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
