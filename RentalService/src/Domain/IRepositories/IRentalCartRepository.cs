using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Contract repository cho aggregate RentalCart.
/// </summary>
public interface IRentalCartRepository
{
    Task<RentalCart?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RentalCart?> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default);

    Task<RentalCart> CreateAsync(RentalCart cart, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(RentalCart cart, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
