using Dprental.PaymentService.Domain.Entities;
namespace Dprental.PaymentService.Domain.IRepositories;

public interface ISettlementLedger
{
    Task<RentalItemSettlementRecord?> FindByItemAsync(Guid rentalItemId, CancellationToken ct = default);
    Task<RentalItemSettlementRecord?> FindByIdWithRowLockAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(RentalItemSettlementRecord record, CancellationToken ct = default);
}
