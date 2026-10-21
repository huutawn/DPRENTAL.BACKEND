using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
namespace Dprental.PaymentService.Domain.Repositories;

public sealed class EfSettlementLedger(PaymentServiceDbContext db) : ISettlementLedger
{
    public Task<RentalItemSettlementRecord?> FindByItemAsync(Guid rentalItemId, CancellationToken ct = default) =>
        db.RentalItemSettlements.AsNoTracking().Include(x => x.RefundRequirement)
            .FirstOrDefaultAsync(x => x.RentalItemId == rentalItemId, ct);

    public Task<RentalItemSettlementRecord?> FindByIdWithRowLockAsync(Guid id, CancellationToken ct = default) =>
        db.RentalItemSettlements.FromSqlInterpolated($"SELECT * FROM rental_item_settlements WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    public async Task AddAsync(RentalItemSettlementRecord record, CancellationToken ct = default) =>
        await db.RentalItemSettlements.AddAsync(record, ct);
}
