using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.PaymentService.Domain.Repository;

public sealed class EfTransactionResolutionRepository(PaymentServiceDbContext db, TimeProvider clock)
    : ITransactionResolutionRepository
{
    public async Task<Result<TransactionResolutionResult>> ResolveAsync(
        ResolveTransactionCommand command,
        Func<PaymentTransaction, Invoice, DateTimeOffset, Result<OutboxMessage>> validateAndCreateEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(validateAndCreateEvent);

        await using var dbTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Lock theo transaction ID để hai admin không thể ghép cùng một khoản tiền hai lần.
            var transactions = await db.PaymentTransactions
                .FromSqlInterpolated($"SELECT * FROM payment_transactions WHERE \"Id\" = {command.TransactionId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var bankTransaction = transactions.SingleOrDefault();
            if (bankTransaction is null)
                return Error.NotFound("Payment.TransactionNotFound", "Không tìm thấy giao dịch.");

            var priorKey = await db.PaymentTransactionResolutions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationKey == command.OperationKey, cancellationToken);
            if (priorKey is not null)
                return ReplayOrConflict(priorKey, bankTransaction, command);

            var priorTransaction = await db.PaymentTransactionResolutions.AsNoTracking()
                .AnyAsync(x => x.TransactionId == bankTransaction.Id, cancellationToken);
            if (priorTransaction)
                return Error.Conflict("Payment.TransactionAlreadyResolved", "Giao dịch đã được đối soát.");

            if (bankTransaction.Status != PaymentTransactionStatus.Unmatched)
                return Error.Conflict("Payment.TransactionNotUnmatched", "Chỉ đối soát giao dịch UNMATCHED.");

            var invoices = await db.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"InvoiceRef\" = {command.InvoiceRef} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var invoice = invoices.SingleOrDefault();
            if (invoice is null)
                return Error.NotFound("Payment.InvoiceNotFound", "Không tìm thấy hóa đơn.");

            var now = clock.GetUtcNow();
            var eventResult = validateAndCreateEvent(bankTransaction, invoice, now);
            if (eventResult.IsFailure) return eventResult.Error;

            var resolution = PaymentTransactionResolution.Create(
                bankTransaction, invoice, command.AdminId, command.OperationKey,
                command.PayloadFingerprint, command.ExternalReference, command.AuditReason, now);
            db.PaymentTransactionResolutions.Add(resolution);
            db.OutboxMessages.Add(eventResult.Value);

            await db.SaveChangesAsync(cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
            return Result.Success(ToResult(resolution, bankTransaction, isReplay: false));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await dbTransaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var prior = await db.PaymentTransactionResolutions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationKey == command.OperationKey, cancellationToken);
            var bankTransaction = await db.PaymentTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == command.TransactionId, cancellationToken);
            if (prior is not null && bankTransaction is not null)
                return ReplayOrConflict(prior, bankTransaction, command);
            return Error.Conflict("Payment.TransactionAlreadyResolved", "Giao dịch đã được đối soát.");
        }
    }

    private static Result<TransactionResolutionResult> ReplayOrConflict(
        PaymentTransactionResolution prior, PaymentTransaction transaction, ResolveTransactionCommand command)
    {
        if (prior.TransactionId != command.TransactionId ||
            prior.PayloadFingerprint != command.PayloadFingerprint)
            return Error.Conflict("Payment.ResolutionOperationKeyConflict",
                "OperationKey đã được dùng cho giao dịch hoặc nội dung khác.");
        return Result.Success(ToResult(prior, transaction, isReplay: true));
    }

    private static TransactionResolutionResult ToResult(
        PaymentTransactionResolution resolution, PaymentTransaction transaction, bool isReplay) =>
        new(resolution.Id, resolution.TransactionId, resolution.InvoiceRef, transaction.PaymentRef,
            resolution.OccurredAt, resolution.ActorId, isReplay);
}
