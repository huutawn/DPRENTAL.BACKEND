using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class EfTransactionJournalRepository : ITransactionJournalRepository
{
    private readonly PaymentServiceDbContext _dbContext;

    public EfTransactionJournalRepository(PaymentServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<TransactionJournalEntry?> GetLatestEntryForStreamAsync(string streamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamId))
        {
            return null;
        }

        return await _dbContext.TransactionJournals
            .Where(j => j.StreamId == streamId)
            .OrderByDescending(j => j.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<TransactionJournalEntry?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TransactionJournals
            .FirstOrDefaultAsync(j => j.EventId == eventId, cancellationToken);
    }

    public async Task<TransactionJournalEntry?> GetByStreamAndVersionAsync(string streamId, long version, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TransactionJournals
            .FirstOrDefaultAsync(j => j.StreamId == streamId && j.Version == version, cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionJournalEntry>> GetStreamEntriesAsync(string streamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamId))
        {
            return [];
        }

        return await _dbContext.TransactionJournals
            .Where(j => j.StreamId == streamId)
            .OrderBy(j => j.Version)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionJournalEntry>> GetPendingConfirmationEntriesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TransactionJournals
            .Where(j => !j.IsConfirmed)
            .OrderBy(j => j.TimestampUtc)
            .ThenBy(j => j.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
    public async Task<TransactionJournalEntry?> GetByOutboxDigestAsync(string outboxDigest, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outboxDigest))
        {
            return null;
        }

        return await _dbContext.TransactionJournals
            .FirstOrDefaultAsync(j => j.OutboxDigest == outboxDigest, cancellationToken);
    }


    public async Task<IReadOnlyList<TransactionJournalEntry>> QueryHistoryAsync(
        string? streamId,
        string? paymentRef,
        string? invoiceRef,
        DateTimeOffset? cursorTimestamp,
        Guid? cursorId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TransactionJournalEntry> query = _dbContext.TransactionJournals.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(streamId))
        {
            query = query.Where(j => j.StreamId == streamId);
        }

        if (!string.IsNullOrWhiteSpace(paymentRef))
        {
            query = query.Where(j => j.PaymentRef == paymentRef);
        }

        if (!string.IsNullOrWhiteSpace(invoiceRef))
        {
            query = query.Where(j => j.InvoiceRef == invoiceRef);
        }

        if (cursorTimestamp.HasValue && cursorId.HasValue)
        {
            var cTime = cursorTimestamp.Value;
            var cId = cursorId.Value;
            query = query.Where(j => j.TimestampUtc < cTime || (j.TimestampUtc == cTime && j.Id.CompareTo(cId) < 0));
        }

        return await query
            .OrderByDescending(j => j.TimestampUtc)
            .ThenByDescending(j => j.Id)
            .Take(Math.Clamp(limit, 1, 101))
            .ToListAsync(cancellationToken);
    }

    public async Task AppendEntryAsync(TransactionJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _dbContext.TransactionJournals.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateConfirmationAsync(
        Guid entryId,
        string proofSignature,
        string signingKeyId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.TransactionJournals.FindAsync([entryId], cancellationToken);
        if (entry is not null)
        {
            entry.Confirm(proofSignature, signingKeyId, confirmedAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
