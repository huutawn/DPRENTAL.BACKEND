using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;

namespace Dprental.PaymentService.Api.Tests.TestHelpers;

public sealed class TestInvoiceRepositoryDecorator : IInvoiceRepository
{
    private readonly IInvoiceRepository _inner;

    public Func<CancellationToken, Task>? OnBeforeFindInvoiceWithRowLock { get; set; }

    public TestInvoiceRepositoryDecorator(IInvoiceRepository inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<Invoice?> GetInvoiceSnapshotByIdAsync(Guid id, CancellationToken cancellationToken = default) => _inner.GetInvoiceSnapshotByIdAsync(id, cancellationToken);
    public Task SaveFinancialOperationAsync(OperationReceipt receipt, IReadOnlyList<TransactionJournalEntry> journals,
        IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken = default) => _inner.SaveFinancialOperationAsync(receipt, journals, messages, cancellationToken);

    public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        _inner.BeginTransactionAsync(cancellationToken);

    public Task<OperationReceipt?> GetOperationReceiptAsync(string operationKey, CancellationToken cancellationToken = default) =>
        _inner.GetOperationReceiptAsync(operationKey, cancellationToken);

    public Task<bool> ExistsByRentalOrderIdAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
        _inner.ExistsByRentalOrderIdAsync(rentalOrderId, cancellationToken);

    public Task<Invoice?> GetInvoiceSnapshotAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
        _inner.GetInvoiceSnapshotAsync(invoiceRef, cancellationToken);

    public Task<bool> IsPaymentRefDuplicateAsync(string paymentRef, string currentInvoiceRef, CancellationToken cancellationToken = default) =>
        _inner.IsPaymentRefDuplicateAsync(paymentRef, currentInvoiceRef, cancellationToken);
    public Task<TransactionJournalEntry?> GetLatestJournalEntryAsync(string streamId, CancellationToken cancellationToken = default) =>
        _inner.GetLatestJournalEntryAsync(streamId, cancellationToken);


    public async Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default)
    {
        if (OnBeforeFindInvoiceWithRowLock is not null)
        {
            await OnBeforeFindInvoiceWithRowLock(cancellationToken);
        }
        return await _inner.FindInvoiceWithRowLockAsync(invoiceRef, cancellationToken);
    }

    public Task<PaymentTransaction?> FindPaymentTransactionWithRowLockAsync(string provider, string providerTransactionId, CancellationToken cancellationToken = default) =>
        _inner.FindPaymentTransactionWithRowLockAsync(provider, providerTransactionId, cancellationToken);

    public Task<Invoice?> FindInvoiceByPaymentCodeWithRowLockAsync(string paymentCode, CancellationToken cancellationToken = default) =>
        _inner.FindInvoiceByPaymentCodeWithRowLockAsync(paymentCode, cancellationToken);

    public Task SavePaymentTransactionAsync(
        PaymentTransaction transaction,
        Invoice? invoice,
        OutboxMessage? outboxMessage,
        TransactionJournalEntry journalEntry,
        TransactionJournalEntry? invoiceJournalEntry,
        CancellationToken cancellationToken = default) =>
        _inner.SavePaymentTransactionAsync(transaction, invoice, outboxMessage, journalEntry, invoiceJournalEntry, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        _inner.GetPendingExpiredCandidateIdsAsync(utcNow, batchSize, cancellationToken);

    public Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.FindInvoiceByIdWithRowLockSkipLockedAsync(id, cancellationToken);

    public Task SaveNewInvoiceAsync(
        Invoice invoice,
        OperationReceipt receipt,
        OutboxMessage? outboxMessage,
        TransactionJournalEntry journalEntry,
        CancellationToken cancellationToken = default) =>
        _inner.SaveNewInvoiceAsync(invoice, receipt, outboxMessage, journalEntry, cancellationToken);

    public Task SaveInvoiceChangesAsync(
        Invoice invoice,
        OperationReceipt? receipt,
        OutboxMessage? outboxMessage,
        TransactionJournalEntry journalEntry,
        CancellationToken cancellationToken = default) =>
        _inner.SaveInvoiceChangesAsync(invoice, receipt, outboxMessage, journalEntry, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _inner.SaveChangesAsync(cancellationToken);
}
