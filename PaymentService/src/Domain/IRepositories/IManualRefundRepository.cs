using Dprental.PaymentService.Domain.Entities;
namespace Dprental.PaymentService.Domain.IRepositories;

public interface IManualRefundRepository
{
    Task<ManualRefundRequirement?> FindAsync(Guid id, CancellationToken ct = default);
    Task<ManualRefundRequirement?> FindWithRowLockAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ManualRefundRequirement>> GetForInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<ManualRefundRequirement>> ListAsync(ManualRefundStatus status, Guid? after, int limit, CancellationToken ct = default);
    Task<bool> TransferReferenceExistsAsync(string reference, Guid exceptId, CancellationToken ct = default);
    Task AddAsync(ManualRefundRequirement refund, CancellationToken ct = default);
}
