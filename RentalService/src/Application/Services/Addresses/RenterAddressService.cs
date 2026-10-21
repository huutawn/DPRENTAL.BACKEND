using Dprental.RentalService.Application.DTOs.Addresses;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Addresses;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Addresses;

/// <summary>
/// Sổ địa chỉ giao của người thuê: tối đa <see cref="MaxAddresses"/> địa chỉ, luôn có đúng một địa chỉ mặc định khi sổ không rỗng.
/// Mọi thao tác sửa đổi khóa theo người thuê để hai yêu cầu cùng lúc không làm sổ có hai địa chỉ mặc định.
/// </summary>
public sealed class RenterAddressService(
    IRenterAddressRepository addresses,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRenterAddressService
{
    public const int MaxAddresses = 10;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<RenterAddressResponse>> ListAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RenterAddress> list = await addresses.ListByRenterAsync(renterId, cancellationToken);
        return list.Select(ToResponse).ToList();
    }

    public async Task<Result<RenterAddressResponse>> CreateAsync(
        Guid renterId,
        SaveRenterAddressRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is { } invalid) return invalid;

        return await WithRenterLockAsync<RenterAddressResponse>(renterId, async () =>
        {
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            if (existing.Count >= MaxAddresses) return RenterAddressErrors.LimitReached;

            // Địa chỉ đầu tiên tự thành mặc định.
            RenterAddress address = new(
                Guid.NewGuid(),
                renterId,
                request.Province!.Trim(),
                request.AddressDetail!.Trim(),
                request.RecipientName!.Trim(),
                request.RecipientPhone!.Trim(),
                isDefault: existing.Count == 0,
                timeProvider.GetUtcNow());
            addresses.Add(address);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(address);
        }, cancellationToken);
    }

    public async Task<Result<RenterAddressResponse>> UpdateAsync(
        Guid renterId,
        Guid addressId,
        SaveRenterAddressRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is { } invalid) return invalid;

        return await WithRenterLockAsync<RenterAddressResponse>(renterId, async () =>
        {
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            RenterAddress? address = existing.FirstOrDefault(a => a.Id == addressId);
            if (address is null) return RenterAddressErrors.NotFound;

            address.Update(
                request.Province!.Trim(),
                request.AddressDetail!.Trim(),
                request.RecipientName!.Trim(),
                request.RecipientPhone!.Trim(),
                timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(address);
        }, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default)
    {
        Result<bool> result = await WithRenterLockAsync<bool>(renterId, async () =>
        {
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            RenterAddress? address = existing.FirstOrDefault(a => a.Id == addressId);
            if (address is null) return RenterAddressErrors.NotFound;

            addresses.Remove(address);
            if (address.IsDefault)
            {
                // Xóa địa chỉ mặc định thì địa chỉ mới nhất còn lại thành mặc định (danh sách đã xếp mới nhất trước).
                existing.FirstOrDefault(a => a.Id != addressId)?.SetDefault(true, timeProvider.GetUtcNow());
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    public async Task<Result<RenterAddressResponse>> SetDefaultAsync(
        Guid renterId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        return await WithRenterLockAsync<RenterAddressResponse>(renterId, async () =>
        {
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            RenterAddress? target = existing.FirstOrDefault(a => a.Id == addressId);
            if (target is null) return RenterAddressErrors.NotFound;

            DateTimeOffset utcNow = timeProvider.GetUtcNow();
            foreach (RenterAddress address in existing)
            {
                address.SetDefault(address.Id == addressId, utcNow);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(target);
        }, cancellationToken);
    }

    private static Error? Validate(SaveRenterAddressRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CheckoutFieldError> errors = [];
        CheckoutRequestValidator.ValidateAddress(
            request.Province, request.AddressDetail, request.RecipientName, request.RecipientPhone, string.Empty, errors);
        return errors.Count == 0
            ? null
            : RenterAddressErrors.InvalidRequest(string.Join(" ", errors.Select(e => e.Message)));
    }

    private async Task<Result<T>> WithRenterLockAsync<T>(
        Guid renterId,
        Func<Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable renterLock;
        try
        {
            renterLock = await locks.AcquireRenterLockAsync(renterId, LockTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return RenterAddressErrors.Busy;
        }

        await using (renterLock)
        {
            return await action();
        }
    }

    private static RenterAddressResponse ToResponse(RenterAddress address) => new(
        address.Id,
        address.Province,
        address.AddressDetail,
        address.RecipientName,
        address.RecipientPhone,
        address.IsDefault);
}
