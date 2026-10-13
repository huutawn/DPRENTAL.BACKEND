using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;

namespace Dprental.PaymentService.Api.Tests.TestHelpers;

public sealed class TestInvoiceRepositoryDecorator : IInvoiceRepository
{
    private readonly IInvoiceRepository _inner;

    public Func<CancellationToken, Task>? OnBeforeFindInvoiceWithRowLock { get; set; }

    public TestInvoiceRepositoryDecorator(IInvoiceRepository inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

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

    public async Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default)
    {
        if (OnBeforeFindInvoiceWithRowLock is not null)
        {
            await OnBeforeFindInvoiceWithRowLock(cancellationToken);
        }
        return await _inner.FindInvoiceWithRowLockAsync(invoiceRef, cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        _inner.GetPendingExpiredCandidateIdsAsync(utcNow, batchSize, cancellationToken);

    public Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.FindInvoiceByIdWithRowLockSkipLockedAsync(id, cancellationToken);

    public Task SaveNewInvoiceAsync(Invoice invoice, OperationReceipt receipt, OutboxMessage? outboxMessage = null, CancellationToken cancellationToken = default) =>
        _inner.SaveNewInvoiceAsync(invoice, receipt, outboxMessage, cancellationToken);

    public Task SaveInvoiceChangesAsync(Invoice invoice, OperationReceipt? receipt = null, OutboxMessage? outboxMessage = null, CancellationToken cancellationToken = default) =>
        _inner.SaveInvoiceChangesAsync(invoice, receipt, outboxMessage, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _inner.SaveChangesAsync(cancellationToken);
}
