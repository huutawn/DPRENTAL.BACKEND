using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepositories;

public interface ITransactionJournalRepository
{
    Task<TransactionJournalEntry?> GetLatestEntryForStreamAsync(string streamId, CancellationToken cancellationToken = default);

    Task<TransactionJournalEntry?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task<TransactionJournalEntry?> GetByStreamAndVersionAsync(string streamId, long version, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionJournalEntry>> GetStreamEntriesAsync(string streamId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionJournalEntry>> GetPendingConfirmationEntriesAsync(int batchSize, CancellationToken cancellationToken = default);
    Task<TransactionJournalEntry?> GetByOutboxDigestAsync(string outboxDigest, CancellationToken cancellationToken = default);


    Task<IReadOnlyList<TransactionJournalEntry>> QueryHistoryAsync(
        string? streamId,
        string? paymentRef,
        string? invoiceRef,
        DateTimeOffset? cursorTimestamp,
        Guid? cursorId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AppendEntryAsync(TransactionJournalEntry entry, CancellationToken cancellationToken = default);

    Task UpdateConfirmationAsync(
        Guid entryId,
        string proofSignature,
        string signingKeyId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default);
}
