using Dprental.RentalService.Application.DTOs.Addresses;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Addresses;

/// <summary>
/// Sổ địa chỉ giao của người thuê. Mọi thao tác chỉ chạm tới địa chỉ của chính người thuê đó.
/// </summary>
public interface IRenterAddressService
{
    Task<IReadOnlyList<RenterAddressResponse>> ListAsync(Guid renterId, CancellationToken cancellationToken = default);

    Task<Result<RenterAddressResponse>> GetAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default);

    Task<Result<RenterAddressResponse>> CreateAsync(Guid renterId, SaveRenterAddressRequest request, CancellationToken cancellationToken = default);

    Task<Result<RenterAddressResponse>> UpdateAsync(Guid renterId, Guid addressId, SaveRenterAddressRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default);

    Task<Result<RenterAddressResponse>> SetDefaultAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default);
}
