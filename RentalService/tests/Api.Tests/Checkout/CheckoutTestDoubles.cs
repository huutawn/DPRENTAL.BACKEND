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

    /// <summary>Số lần lấy mã tiếp theo bị lỗi như sequence PostgreSQL không phản hồi.</summary>
    public int FailNext { get; set; }

    public Task<string> NextAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("Simulated database outage.");
        }

        return Task.FromResult($"DH-{utcNow.Year}-{Interlocked.Increment(ref _issued):D6}");
    }
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

    public Task<IReadOnlyList<Guid>> GetRenterResponseTimeoutItemIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> GetDisputedItemIdsAsync(
        int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> GetSettlementReadyItemIdsAsync(
        int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<RenterCalendarItemRecord>> GetRenterCalendarItemsAsync(
        Guid renterId,
        DateOnly monthStart,
        DateOnly monthEnd,
        IReadOnlyCollection<RentalItemStatus> targetStatuses,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalSubOrder?> GetSubOrderByIdAsync(Guid subOrderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RecordPage<RenterOrderSummaryRecord>> GetRenterOrderPageAsync(
        Guid renterId, RentalOrderStatus? status, int skip, int take, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(
        Guid orderId, Guid renterId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(
        Guid supplierId, RentalItemStatus? itemStatus, int skip, int take, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RecordPage<AdminOrderSummaryRecord>> SearchOrdersForAdminAsync(
        AdminOrderSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalOrder?> GetOrderForAdminAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RentalSubOrder?> GetPaidSubOrderForSupplierAsync(
        Guid subOrderId, Guid supplierId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>
/// Inventory giả phục vụ bước chuẩn bị checkout: đọc thông tin máy (rental-snapshot) và hỏi lịch trống.
/// ListingReadCount đếm số lần gọi đọc Listing; AvailabilityCheckCount đếm số lần hỏi lịch (mỗi dòng một lần).
/// </summary>
internal sealed class FakeInventoryListingClient : IInventoryRentalClient
{
    private static readonly Error Down = Error.ServiceUnavailable("Inventory.Unavailable", "Inventory không phản hồi.");

    public Dictionary<Guid, ListingRentalSnapshotDto> Listings { get; } = [];

    public Dictionary<Guid, int> AvailableQuantities { get; } = [];

    public bool FailListingRead { get; set; }

    public bool FailAvailabilityCheck { get; set; }

    public int ListingReadCount { get; private set; }

    public int AvailabilityCheckCount { get; private set; }

    public List<(DateOnly StartDate, DateOnly EndDate)> AvailabilityPeriods { get; } = [];

    public List<Guid> RequestedListingIds { get; } = [];

    public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(
        ListingRentalSnapshotsRequest request,
        CancellationToken ct = default)
    {
        ListingReadCount++;
        RequestedListingIds.AddRange(request.EquipmentListingIds);
        if (FailListingRead)
        {
            return Task.FromResult(Result.Failure<ListingRentalSnapshotsResponse>(Down));
        }

        List<ListingRentalSnapshotDto> found = request.EquipmentListingIds.Where(Listings.ContainsKey).Select(id => Listings[id]).ToList();
        List<Guid> notFound = request.EquipmentListingIds.Where(id => !Listings.ContainsKey(id)).ToList();
        return Task.FromResult(Result.Success(new ListingRentalSnapshotsResponse(found, notFound)));
    }

    public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default)
    {
        ListingReadCount++;
        if (FailListingRead)
        {
            return Task.FromResult(Result.Failure<ListingRentalSnapshotDto>(Down));
        }

        return Task.FromResult(Listings.TryGetValue(listingId, out ListingRentalSnapshotDto? listing)
            ? Result.Success(listing)
            : Result.Failure<ListingRentalSnapshotDto>(Error.NotFound(
                InventoryContractSpec.ErrorCodes.ListingNotFound, "Không tìm thấy Listing.")));
    }

    public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(
        ListingAvailabilityCheckRequest request,
        CancellationToken ct = default)
    {
        AvailabilityCheckCount++;
        AvailabilityPeriods.Add((request.StartDate, request.EndDate));
        if (FailAvailabilityCheck)
        {
            return Task.FromResult(Result.Failure<ListingAvailabilityCheckResponse>(Down));
        }

        int available = AvailableQuantities.GetValueOrDefault(request.ListingId);
        return Task.FromResult(Result.Success(new ListingAvailabilityCheckResponse(
            request.ListingId, request.StartDate, request.EndDate, request.RequestedQuantity,
            available, available >= request.RequestedQuantity)));
    }

    public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Thông tin máy theo hợp đồng Inventory cho test checkout.</summary>
internal static class TestListings
{
    public static readonly Guid EquipmentTypeId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid BrandId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    public static ListingRentalSnapshotDto Create(
        Guid listingId,
        Guid supplierId,
        string name,
        decimal dailyRate,
        bool isActive = true,
        Guid? thumbnailMediaId = null,
        string currency = "VND",
        decimal depositPerUnit = 0m) => new(
        listingId,
        supplierId,
        name,
        thumbnailMediaId,
        dailyRate,
        currency,
        isActive ? "Active" : "Inactive",
        EquipmentTypeId: EquipmentTypeId,
        BrandId: BrandId,
        Model: "A7 IV",
        DepositPerUnit: depositPerUnit,
        SpecSchemaVersion: 1,
        SpecValues: System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"resolutionMp\":33}"),
        EquipmentTypeName: "Camera",
        BrandName: "Sony");
}
