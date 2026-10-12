using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Api.Tests.Checkout;

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class FakeCartRepository : IRentalCartRepository
{
    public RentalCart? Cart { get; set; }

    public Task<RentalCart?> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Cart?.RenterId == renterId ? Cart : null);

    public Task<RentalCart?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalCart> CreateAsync(RentalCart cart, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> UpdateAsync(RentalCart cart, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeOrderRepository : IRentalOrderRepository
{
    public RentalOrder? PendingPaymentOrder { get; set; }

    public Task<RentalOrder?> GetPendingPaymentByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PendingPaymentOrder?.RenterId == renterId ? PendingPaymentOrder : null);

    public Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder?> GetByOrderCodeAsync(string orderCode, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder?> GetByCheckoutOperationIdAsync(string operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder?> GetByItemIdWithDetailsAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Guid?> GetOrderIdByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> GetPendingPaymentExpiredOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> GetPendingSupplierTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<RenterCalendarItemRecord>> GetRenterCalendarItemsAsync(
        Guid renterId,
        DateOnly monthStart,
        DateOnly monthEnd,
        IReadOnlyCollection<RentalItemStatus> targetStatuses,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeInventoryListingClient : IInventoryListingClient
{
    private static readonly Error Down = Error.ServiceUnavailable("Inventory.Unavailable", "Inventory không phản hồi.");

    public Dictionary<Guid, InventoryListingDto> Listings { get; } = [];

    public Dictionary<Guid, int> AvailableQuantities { get; } = [];

    public bool FailListingRead { get; set; }

    public bool FailAvailabilityCheck { get; set; }

    public int ListingReadCount { get; private set; }

    public int AvailabilityCheckCount { get; private set; }

    public Task<Result<InventoryListingDto?>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        ListingReadCount++;
        if (FailListingRead)
        {
            return Task.FromResult(Result.Failure<InventoryListingDto?>(Down));
        }

        return Task.FromResult(Result.Success(Listings.GetValueOrDefault(listingId)));
    }

    public Task<Result<InventoryListingAvailabilityDto>> CheckAvailabilityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        int requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        AvailabilityCheckCount++;
        if (FailAvailabilityCheck)
        {
            return Task.FromResult(Result.Failure<InventoryListingAvailabilityDto>(Down));
        }

        int available = AvailableQuantities.GetValueOrDefault(listingId);
        return Task.FromResult(Result.Success(
            new InventoryListingAvailabilityDto(listingId, available, available >= requestedQuantity)));
    }
}

internal sealed class FakeCatalogReferenceClient : ICatalogReferenceClient
{
    private static readonly Error Down = Error.ServiceUnavailable("Catalog.Unavailable", "Catalog không phản hồi.");

    public Dictionary<Guid, string> EquipmentTypeNames { get; } = [];

    public Dictionary<Guid, string> BrandNames { get; } = [];

    public bool Fail { get; set; }

    public int CallCount { get; private set; }

    public Task<Result<string?>> GetEquipmentTypeNameAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(Fail
            ? Result.Failure<string?>(Down)
            : Result.Success(EquipmentTypeNames.GetValueOrDefault(equipmentTypeId)));
    }

    public Task<Result<string?>> GetBrandNameAsync(Guid brandId, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(Fail
            ? Result.Failure<string?>(Down)
            : Result.Success(BrandNames.GetValueOrDefault(brandId)));
    }
}
