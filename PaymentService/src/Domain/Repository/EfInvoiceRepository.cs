using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Dprental.PaymentService.Domain.Persistence;

namespace Dprental.PaymentService.Domain.Repository;

public sealed class EfInvoiceRepository : IInvoiceRepository
{
    private readonly PaymentServiceDbContext _dbContext;

    public EfInvoiceRepository(PaymentServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new EfExecutionTransaction(transaction, _dbContext);
    }

    public async Task<OperationReceipt?> GetOperationReceiptAsync(string operationKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return null;
        }

        return await _dbContext.OperationReceipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OperationKey == operationKey, cancellationToken);
    }

    public async Task<bool> ExistsByRentalOrderIdAsync(Guid rentalOrderId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Invoices
            .AsNoTracking()
            .AnyAsync(i => i.RentalOrderId == rentalOrderId, cancellationToken);
    }

    public async Task<Invoice?> GetInvoiceSnapshotAsync(string invoiceRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef))
        {
            return null;
        }

        return await _dbContext.Invoices
            .Include(i => i.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.InvoiceRef == invoiceRef, cancellationToken);
    }

    public async Task<bool> IsPaymentRefDuplicateAsync(string paymentRef, string currentInvoiceRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paymentRef))
        {
            return false;
        }

        return await _dbContext.Invoices
            .AsNoTracking()
            .AnyAsync(i => i.PaymentRef == paymentRef && i.InvoiceRef != currentInvoiceRef, cancellationToken);
    }

    public async Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef))
        {
            return null;
        }

        Invoice? locked;
        if (_dbContext.Database.IsNpgsql())
        {
            locked = await _dbContext.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"InvoiceRef\" = {invoiceRef} FOR UPDATE")
                .Include(i => i.Items)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            locked = await _dbContext.Invoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.InvoiceRef == invoiceRef, cancellationToken);
        }

        if (locked is not null)
        {
            await _dbContext.Entry(locked).ReloadAsync(cancellationToken);
        }

        return locked;
    }

    public async Task<PaymentTransaction?> FindPaymentTransactionWithRowLockAsync(
        string provider,
        string providerTransactionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerTransactionId))
        {
            return null;
        }

        PaymentTransaction? locked;
        if (_dbContext.Database.IsNpgsql())
        {
            locked = await _dbContext.PaymentTransactions
                .FromSqlInterpolated($"SELECT * FROM payment_transactions WHERE \"Provider\" = {provider} AND \"ProviderTransactionId\" = {providerTransactionId} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            locked = await _dbContext.PaymentTransactions
                .FirstOrDefaultAsync(t => t.Provider == provider && t.ProviderTransactionId == providerTransactionId, cancellationToken);
        }

        if (locked is not null)
        {
            await _dbContext.Entry(locked).ReloadAsync(cancellationToken);
        }

        return locked;
    }

    public async Task<Invoice?> FindInvoiceByPaymentCodeWithRowLockAsync(
        string paymentCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paymentCode))
        {
            return null;
        }

        Invoice? locked;
        if (_dbContext.Database.IsNpgsql())
        {
            locked = await _dbContext.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"PaymentCode\" = {paymentCode} FOR UPDATE")
                .Include(i => i.Items)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            locked = await _dbContext.Invoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.PaymentCode == paymentCode, cancellationToken);
        }

        if (locked is not null)
        {
            await _dbContext.Entry(locked).ReloadAsync(cancellationToken);
        }

        return locked;
    }

    public async Task SavePaymentTransactionAsync(
        PaymentTransaction transaction,
        Invoice? invoice = null,
        OutboxMessage? outboxMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (_dbContext.Entry(transaction).State == EntityState.Detached)
        {
            await _dbContext.PaymentTransactions.AddAsync(transaction, cancellationToken);
        }

        if (invoice is not null)
        {
            if (_dbContext.Entry(invoice).State == EntityState.Detached)
            {
                _dbContext.Invoices.Update(invoice);
            }
        }

        if (outboxMessage is not null)
        {
            await _dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbExceptionTranslator.TryTranslate(ex, out var constraintEx))
        {
            throw constraintEx;
        }
    }

    public async Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Pending && i.PaymentDeadline <= utcNow)
            .Take(batchSize)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Invoice? locked;
        if (_dbContext.Database.IsNpgsql())
        {
            locked = await _dbContext.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"Id\" = {id} FOR UPDATE SKIP LOCKED")
                .Include(i => i.Items)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            locked = await _dbContext.Invoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        }

        if (locked is not null)
        {
            await _dbContext.Entry(locked).ReloadAsync(cancellationToken);
        }

        return locked;
    }

    public async Task SaveNewInvoiceAsync(
        Invoice invoice,
        OperationReceipt receipt,
        OutboxMessage? outboxMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(receipt);

        try
        {
            _dbContext.Invoices.Add(invoice);
            _dbContext.OperationReceipts.Add(receipt);
            if (outboxMessage is not null)
            {
                _dbContext.OutboxMessages.Add(outboxMessage);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _dbContext.ChangeTracker.Clear();
            if (DbExceptionTranslator.TryTranslate(ex, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
        }
    }

    public async Task SaveInvoiceChangesAsync(
        Invoice invoice,
        OperationReceipt? receipt = null,
        OutboxMessage? outboxMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        try
        {
            if (receipt is not null)
            {
                _dbContext.OperationReceipts.Add(receipt);
            }

            if (outboxMessage is not null)
            {
                _dbContext.OutboxMessages.Add(outboxMessage);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _dbContext.ChangeTracker.Clear();
            if (DbExceptionTranslator.TryTranslate(ex, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
        }
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _dbContext.ChangeTracker.Clear();
            if (DbExceptionTranslator.TryTranslate(ex, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
        }
    }

    private sealed class EfExecutionTransaction : IExecutionTransaction
    {
        private readonly IDbContextTransaction _transaction;
        private readonly PaymentServiceDbContext _dbContext;
        private bool _completed;

        public EfExecutionTransaction(IDbContextTransaction transaction, PaymentServiceDbContext dbContext)
        {
            _transaction = transaction;
            _dbContext = dbContext;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            _completed = true;
            await _transaction.CommitAsync(cancellationToken);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            _completed = true;
            await _transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                _dbContext.ChangeTracker.Clear();
            }
            await _transaction.DisposeAsync();
        }
    }
}
