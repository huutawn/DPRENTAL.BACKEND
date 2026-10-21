using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
namespace Dprental.PaymentService.Domain.Repositories;

public sealed class ManualRefundRepository(PaymentServiceDbContext db) : IManualRefundRepository
{
    public Task<ManualRefundRequirement?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.ManualRefundRequirements.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task<ManualRefundRequirement?> FindWithRowLockAsync(Guid id, CancellationToken ct = default) =>
        db.ManualRefundRequirements.FromSqlInterpolated($"SELECT * FROM manual_refund_requirements WHERE \"Id\" = {id} FOR UPDATE")
            .Include(x => x.Items).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ManualRefundRequirement>> GetForInvoiceAsync(Guid invoiceId, CancellationToken ct = default) =>
        await db.ManualRefundRequirements.AsNoTracking().Include(x => x.Items).Where(x => x.InvoiceId == invoiceId).ToListAsync(ct);

    public async Task<IReadOnlyList<ManualRefundRequirement>> ListAsync(ManualRefundStatus status, Guid? after, int limit, CancellationToken ct = default) =>
        await db.ManualRefundRequirements.AsNoTracking().Include(x => x.Items)
            .Where(x => x.Status == status && (!after.HasValue || x.Id.CompareTo(after.Value) > 0))
            .OrderBy(x => x.Id).Take(limit).ToListAsync(ct);

    public Task<bool> TransferReferenceExistsAsync(string reference, Guid exceptId, CancellationToken ct = default) =>
        db.ManualRefundRequirements.AsNoTracking().AnyAsync(x => x.Id != exceptId && x.ExternalTransferReference == reference, ct);

    public async Task AddAsync(ManualRefundRequirement refund, CancellationToken ct = default) =>
        await db.ManualRefundRequirements.AddAsync(refund, ct);
}
