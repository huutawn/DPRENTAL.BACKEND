using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Api.Tests.Checkout;

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class FakeOrderCodeGenerator : IOrderCodeGenerator
{
    private int _issued;

    public Task<string> NextAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) =>
        Task.FromResult($"DH-{utcNow.Year}-{Interlocked.Increment(ref _issued):D6}");
}

/// <summary>Đơn chờ thanh toán dựng sẵn cho test hóa đơn: tiền thuê 3.600.000, tiền cọc 5.000.000.</summary>
internal static class CheckoutTestOrders
{
    public const decimal TotalRentalAmount = 3_600_000m;
    public const decimal TotalDepositAmount = 5_000_000m;

    public static RentalOrder CreatePending(Guid renterId, string checkoutOperationId, DateTimeOffset utcNow)
    {
        DateOnly start = new(2026, 10, 12);
        DateOnly end = new(2026, 10, 15);

        return RentalOrder.Create(
            Guid.NewGuid(),
            "DH-2026-000042",
            renterId,
            "VND",
            "Delivery",
            "Nguyễn Văn A",
            "0901234567",
            "123 Nguyễn Huệ, Quận 1, Hồ Chí Minh",
            null,
            null,
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            checkoutOperationId,
            utcNow,
            [
                Item("Sony A7 IV", 2, 500_000m, 2_000_000m, "grp-camera"),
                Item("Ống kính 24-70", 1, 200_000m, 1_000_000m, "grp-lens")
            ]).Value;

        InitialRentalItemDto Item(string title, int quantity, decimal dailyRate, decimal depositPerUnit, string groupId) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            quantity,
            start,
            end,
            title,
            null,
            "Quận 1, TP.HCM",
            dailyRate,
            depositPerUnit,
            groupId,
            Enumerable.Range(0, quantity)
                .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
                .ToList(),
            TestListingSnapshots.Default);
    }
}

internal sealed class FakeCartRepository : IRentalCartRepository
{
    public RentalCart? Cart { get; set; }

    public Task<RentalCart?> GetActiveByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Cart?.RenterId == renterId && Cart.Status == RentalCartStatus.Active ? Cart : null);

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

    public Task<SupplierConfirmationStatisticsRecord> GetSupplierConfirmationStatisticsAsync(
        Guid supplierId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SupplierConfirmationStatisticsRecord(0, 0, 0, 0));

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
    public Task<IReadOnlyList<Guid>> GetOverdueUnprotectedOrderIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<Guid>>([]);
    public Task<IReadOnlyList<Guid>> GetDeductionTimeoutItemIdsAsync(
        DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

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

    public List<(DateOnly StartDate, DateOnly EndDate)> AvailabilityPeriods { get; } = [];

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
        AvailabilityPeriods.Add((startDate, endDate));
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
