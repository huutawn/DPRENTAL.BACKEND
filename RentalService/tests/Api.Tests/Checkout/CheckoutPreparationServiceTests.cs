using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class CheckoutPreparationServiceTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 10, 12);
    // Giỏ lưu ngày trả có tính: giao 12/10, trả 14/10 là 3 ngày (12, 13, 14).
    private static readonly DateOnly End = new(2026, 10, 14);

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _thumbnailMediaId = Guid.NewGuid();

    private readonly FakeCartRepository _carts = new();
    private readonly FakeOrderRepository _orders = new();
    private readonly FakeRenterAddressRepository _addresses = new();
    private readonly FakeInventoryListingClient _inventory = new();
    private readonly RentalCart _cart;
    private readonly Guid _cartItemId;

    public CheckoutPreparationServiceTests()
    {
        _cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        _cartItemId = AddCartItem(_listingId, 2, Start, End);
        _carts.Cart = _cart;

        _inventory.Listings[_listingId] = Listing(_listingId);
        _inventory.AvailableQuantities[_listingId] = 10;
    }

    private Guid AddCartItem(Guid listingId, int quantity, DateOnly start, DateOnly end) =>
        _cart.AddItem(Guid.NewGuid(), listingId, _supplierId, quantity, start, end, Now).Value.Id;

    private ListingRentalSnapshotDto Listing(Guid id) =>
        TestListings.Create(id, _supplierId, " Sony A7 IV ", 500_000m, thumbnailMediaId: _thumbnailMediaId);

    // Fixture này dùng cọc = 0; test riêng xác nhận cọc khác 0 từ Inventory.
    private CheckoutRequest Request(decimal expectedTotal = 3_000_000m, params Guid[] cartItemIds) => new(
        cartItemIds.Length == 0 ? [_cartItemId] : cartItemIds,
        new(" Hồ Chí Minh ", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", "  "),
        new("VCB", "0123456789", "NGUYEN VAN A"),
        "v1.0",
        true,
        expectedTotal);

    private CheckoutRequest BuyNowRequest(decimal expectedTotal, DateOnly? start = null) =>
        Request(expectedTotal) with
        {
            CartItemIds = null,
            BuyNow = new(_listingId, 1, start ?? Start, End)
        };

    private CheckoutRenterContext Renter(bool profileCompleted = true) => new(_renterId, profileCompleted);

    private CheckoutPreparationService CreateService(DateTimeOffset? utcNow = null, IInventoryRentalClient? inventory = null) => new(
        _carts,
        _orders,
        _addresses,
        inventory ?? _inventory,
        Microsoft.Extensions.Options.Options.Create(new RentalTermsOptions
        {
            CurrentVersion = "v1.0",
            DocumentRef = "terms/v1.0.pdf",
            ContentHash = "hash123456"
        }),
        new FixedTimeProvider(utcNow ?? Now));

    private RentalOrder CreatePendingOrder(DateTimeOffset createdAt) =>
        RentalOrder.Create(
            Guid.NewGuid(),
            "DH-2026-000007",
            _renterId,
            "VND",
            "Delivery",
            "Nguyen Van A",
            "0901234567",
            "123 Nguyen Hue",
            null,
            "bank-ref",
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            "op-previous",
            createdAt,
            [
                new InitialRentalItemDto(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    _supplierId,
                    1,
                    Start,
                    End,
                    "Lens",
                    null,
                    "Pickup",
                    100_000m,
                    200_000m,
                    "rental-item:previous",
                    [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                    TestListingSnapshots.Default)
            ]).Value;

    [Fact]
    public async Task Prepare_ReturnsPlanWithSnapshotAndTotals()
    {
        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.True(result.IsSuccess);
        CheckoutPlan plan = result.Plan;
        Assert.Equal(_renterId, plan.RenterId);
        Assert.Equal(_cart.Id, plan.CartId);
        Assert.Equal("VND", plan.Currency);
        Assert.Equal("v1.0", plan.TermsVersion);
        Assert.Equal("terms/v1.0.pdf", plan.TermsDocumentRef);
        Assert.Equal("hash123456", plan.TermsContentHash);
        Assert.Equal("Hồ Chí Minh", plan.Delivery.Province);
        Assert.Null(plan.Delivery.Note);

        CheckoutPlanLine line = Assert.Single(plan.Lines);
        Assert.Equal(_cartItemId, line.CartItemId);
        Assert.Equal(_listingId, line.EquipmentListingId);
        Assert.Equal(_supplierId, line.SupplierId);
        Assert.Equal("Sony A7 IV", line.ListingName);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(3, line.RentalDays);
        // Đơn hàng lưu [từ, đến): ngày kết thúc lùi một ngày so với ngày trả trong giỏ.
        Assert.Equal(new DateOnly(2026, 10, 12), line.ExpectedStartDate);
        Assert.Equal(new DateOnly(2026, 10, 15), line.ExpectedEndDate);
        // Inventory nhận ngày dùng máy cuối có tính, giống giỏ: 12, 13, 14.
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14)), Assert.Single(_inventory.AvailabilityPeriods));
        Assert.Equal(500_000m, line.DailyRate);
        Assert.Equal(0m, line.DepositPerUnit);
        Assert.Equal(3_000_000m, line.RentalAmount);
        Assert.Equal(0m, line.DepositAmount);
        Assert.Equal(TestListings.EquipmentTypeId, line.ListingSnapshot.EquipmentTypeId);
        Assert.Equal(TestListings.BrandId, line.ListingSnapshot.BrandId);
        Assert.Equal("A7 IV", line.ListingSnapshot.Model);
        Assert.Equal("{\"resolutionMp\":33}", line.ListingSnapshot.SpecValuesJson);
        Assert.Equal(_thumbnailMediaId, line.ListingSnapshot.ThumbnailMediaId);

        Assert.Equal(3_000_000m, plan.TotalRentalAmount);
        Assert.Equal(0m, plan.TotalDepositAmount);
        Assert.Equal(3_000_000m, plan.TotalAmount);
    }

    [Fact]
    public async Task Prepare_IncludesInventoryDepositInCheckoutQuote()
    {
        _inventory.Listings[_listingId] = Listing(_listingId) with { DepositPerUnit = 2_000_000m };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(7_000_000m));

        Assert.True(result.IsSuccess);
        CheckoutPlanLine line = Assert.Single(result.Plan.Lines);
        Assert.Equal(2_000_000m, line.DepositPerUnit);
        Assert.Equal(4_000_000m, line.DepositAmount);
        Assert.Equal(7_000_000m, result.Plan.TotalAmount);
    }

    [Fact]
    public async Task Prepare_PlanIsAcceptedByRentalOrderFactory()
    {
        CheckoutPlanLine line = (await CreateService().PrepareAsync(Renter(), Request())).Plan.Lines.Single();

        Result<RentalOrder> order = RentalOrder.Create(
            Guid.NewGuid(),
            "DH-2026-000001",
            _renterId,
            "VND",
            "Delivery",
            "Nguyễn Văn A",
            "0901234567",
            "123 Nguyễn Huệ, Quận 1, Hồ Chí Minh",
            null,
            "bank-ref",
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            "op-checkout",
            Now,
            [
                new InitialRentalItemDto(
                    Guid.NewGuid(),
                    line.EquipmentListingId,
                    line.SupplierId,
                    line.Quantity,
                    line.ExpectedStartDate,
                    line.ExpectedEndDate,
                    line.ListingName,
                    null,
                    null,
                    line.DailyRate,
                    line.DepositPerUnit,
                    "rental-item:1",
                    Enumerable.Range(0, line.Quantity)
                        .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
                        .ToList(),
                    line.ListingSnapshot)
            ]);

        Assert.True(order.IsSuccess);
        RentalItem item = order.Value.Items.Single();
        Assert.Equal(line.RentalDays, item.RentalDays);
        Assert.Equal(line.RentalAmount, item.RentalAmount);
        Assert.Equal(line.DepositAmount, item.DepositAmount);
    }

    [Fact]
    public async Task Prepare_SupportsSingleDayRental()
    {
        Guid oneDayItem = AddCartItem(_listingId, 1, new(2026, 10, 20), new(2026, 10, 20));

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(500_000m, oneDayItem));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Plan.Lines.Single().RentalDays);
        Assert.Equal(500_000m, result.Plan.TotalRentalAmount);
    }

    [Fact]
    public async Task Prepare_RejectsIncompleteProfile_BeforeCallingInventory()
    {
        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(profileCompleted: false), Request());

        Assert.Equal(CheckoutErrors.ProfileIncomplete, result.Error);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_RejectsMissingRenterIdentity()
    {
        CheckoutPreparationResult result = await CreateService().PrepareAsync(new(Guid.Empty, true), Request());

        Assert.Equal(CheckoutErrors.Unauthenticated, result.Error);
    }

    [Fact]
    public async Task Prepare_WithWarehouseInListingSnapshot_FormatsPickupLocationWithCoordinates()
    {
        _inventory.Listings[_listingId] = _inventory.Listings[_listingId] with
        {
            Warehouse = new ListingWarehouseDto("123 Le Loi", "79", "Thành phố Hồ Chí Minh", "25747", "Phường Thủ Dầu Một", 10.7769, 106.7009)
        };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.Equal("123 Le Loi, Phường Thủ Dầu Một, Thành phố Hồ Chí Minh (10.776900, 106.700900)", result.Plan.Lines[0].PickupLocationSnapshot);
    }

    [Fact]
    public async Task Prepare_ReturnsFieldErrors_BeforeCallingInventory()
    {
        CheckoutRequest request = Request() with { TermsAccepted = false, RefundAccount = new("VCB", "12", "NGUYEN VAN A") };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), request);

        Assert.Equal(CheckoutErrors.InvalidRequest, result.Error);
        Assert.Equal(
            ["refundAccount.accountNumber", "termsAccepted"],
            result.FieldErrors.Select(e => e.Field).Order(StringComparer.Ordinal));
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_RejectsOutdatedTermsVersion()
    {
        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request() with { TermsVersion = "v0.9" });

        Assert.Equal(CheckoutErrors.TermsVersionOutdated, result.Error);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_RejectsWhenRenterHasUnexpiredPendingPaymentOrder()
    {
        _orders.PendingPaymentOrder = CreatePendingOrder(Now.AddMinutes(-10));

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.PendingOrderExists, result.Error);
        Assert.Equal("DH-2026-000007", result.PendingOrderCode);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_IgnoresPendingPaymentOrderPastItsDeadline()
    {
        _orders.PendingPaymentOrder = CreatePendingOrder(Now.AddMinutes(-31));

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Prepare_RejectsWhenCartDoesNotExist()
    {
        _carts.Cart = null;

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(RentalErrors.CartNotFound, result.Error);
    }

    [Fact]
    public async Task Prepare_RejectsCartItemThatIsNotInRentersCart()
    {
        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(3_000_000m, Guid.NewGuid()));

        Assert.Equal(RentalErrors.CartItemNotFound, result.Error);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_RejectsStartDateThatIsTodayInVietnamTime()
    {
        // 17:30 UTC ngày 11/10 là 00:30 ngày 12/10 giờ Việt Nam, nên ngày giao 12/10 đã là "hôm nay".
        DateTimeOffset justAfterMidnightIct = new(2026, 10, 11, 17, 30, 0, TimeSpan.Zero);

        CheckoutPreparationResult result = await CreateService(justAfterMidnightIct).PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.InvalidRentalDates, result.Error);
        CheckoutLineIssue issue = Assert.Single(result.LineIssues);
        Assert.Equal(CheckoutLineIssueCode.StartDateNotInFuture, issue.Code);
        Assert.Equal(_cartItemId, issue.CartItemId);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_RejectsStartDateLessThanThreeDaysAheadInVietnamTime()
    {
        // 17:30 UTC ngày 09/10 là 00:30 ngày 10/10 giờ Việt Nam: ngày thuê sớm nhất là 13/10, nên 12/10 bị chặn.
        DateTimeOffset justAfterMidnightIct = new(2026, 10, 9, 17, 30, 0, TimeSpan.Zero);

        CheckoutPreparationResult result = await CreateService(justAfterMidnightIct).PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.InvalidRentalDates, result.Error);
        CheckoutLineIssue issue = Assert.Single(result.LineIssues);
        Assert.Equal(CheckoutLineIssueCode.StartDateNotInFuture, issue.Code);
        Assert.Equal("Phải đặt trước ít nhất 3 ngày, ngày bắt đầu thuê sớm nhất là 13/10/2026.", issue.Message);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_AcceptsStartDateExactlyThreeDaysAheadInVietnamTime()
    {
        // 16:30 UTC ngày 09/10 là 23:30 ngày 09/10 giờ Việt Nam: ngày thuê sớm nhất là 12/10.
        DateTimeOffset beforeMidnightIct = new(2026, 10, 9, 16, 30, 0, TimeSpan.Zero);

        CheckoutPreparationResult result = await CreateService(beforeMidnightIct).PrepareAsync(Renter(), Request());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Prepare_RejectsRentalLongerThan30Days_AndStartBeyondSixMonths()
    {
        Guid tooLong = AddCartItem(_listingId, 1, new(2026, 11, 1), new(2026, 12, 1));
        Guid thirtyDays = AddCartItem(_listingId, 1, new(2026, 11, 1), new(2026, 11, 30));
        Guid tooFar = AddCartItem(_listingId, 1, new(2027, 4, 10), new(2027, 4, 11));

        CheckoutPreparationResult result = await CreateService()
            .PrepareAsync(Renter(), Request(0m, tooLong, thirtyDays, tooFar));

        Assert.Equal(CheckoutErrors.InvalidRentalDates, result.Error);
        Assert.Equal(
            [(tooLong, CheckoutLineIssueCode.RentalPeriodTooLong), (tooFar, CheckoutLineIssueCode.StartDateBeyondHorizon)],
            result.LineIssues.Select(i => (i.CartItemId, i.Code)));
    }

    [Fact]
    public async Task Prepare_CollectsIssuesOfEveryLine()
    {
        Guid inactiveListing = Guid.NewGuid();
        Guid missingListing = Guid.NewGuid();
        Guid ownListing = Guid.NewGuid();
        Guid scarceListing = Guid.NewGuid();
        _inventory.Listings[inactiveListing] = Listing(inactiveListing) with { Status = "Inactive" };
        _inventory.Listings[ownListing] = Listing(ownListing) with { OwnerUserId = _renterId };
        _inventory.Listings[scarceListing] = Listing(scarceListing);
        _inventory.AvailableQuantities[scarceListing] = 3;

        Guid inactiveItem = AddCartItem(inactiveListing, 1, Start, End);
        Guid missingItem = AddCartItem(missingListing, 1, Start, End);
        Guid ownItem = AddCartItem(ownListing, 1, Start, End);
        Guid scarceItem = AddCartItem(scarceListing, 5, Start, End);

        CheckoutPreparationResult result = await CreateService()
            .PrepareAsync(Renter(), Request(0m, _cartItemId, inactiveItem, missingItem, ownItem, scarceItem));

        Assert.Equal(CheckoutErrors.ItemsUnavailable, result.Error);
        Assert.Equal(
            [
                (inactiveItem, CheckoutLineIssueCode.ListingUnavailable),
                (missingItem, CheckoutLineIssueCode.ListingUnavailable),
                (ownItem, CheckoutLineIssueCode.OwnListing),
                (scarceItem, CheckoutLineIssueCode.InsufficientAvailability)
            ],
            result.LineIssues.Select(i => (i.CartItemId, i.Code)));

        CheckoutLineIssue scarce = result.LineIssues.Single(i => i.CartItemId == scarceItem);
        Assert.Equal(5, scarce.RequestedQuantity);
        Assert.Equal(3, scarce.AvailableQuantity);
    }

    [Theory]
    [InlineData(0, "VND")]
    [InlineData(500_000, "USD")]
    public async Task Prepare_RejectsListingWithoutUsablePricing(int dailyRate, string currency)
    {
        _inventory.Listings[_listingId] = Listing(_listingId) with
        {
            DailyRate = dailyRate,
            Currency = currency
        };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.ItemsUnavailable, result.Error);
        Assert.Equal(CheckoutLineIssueCode.ListingUnavailable, Assert.Single(result.LineIssues).Code);
    }

    public static TheoryData<string> IncompleteSnapshotFields => ["OwnerUserId", "Name", "Status", "Currency", "BrandId", "Model", "DepositPerUnit", "SpecSchemaVersion", "SpecValues"];

    [Theory]
    [MemberData(nameof(IncompleteSnapshotFields))]
    public async Task Prepare_WhenInventorySnapshotMissesContractFields_ReportsDependencyUnavailable(string missingField)
    {
        ListingRentalSnapshotDto listing = Listing(_listingId);
        _inventory.Listings[_listingId] = missingField switch
        {
            "OwnerUserId" => listing with { OwnerUserId = Guid.Empty },
            "Name" => listing with { Name = " " },
            "Status" => listing with { Status = null! },
            "EquipmentTypeId" => listing with { EquipmentTypeId = Guid.Empty },
            "BrandId" => listing with { BrandId = null },
            "Model" => listing with { Model = null },
            "DepositPerUnit" => listing with { DepositPerUnit = null },
            "SpecSchemaVersion" => listing with { SpecSchemaVersion = null },
            "SpecValues" => listing with { SpecValues = null },
            _ => listing with { Currency = null! }
        };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        // Inventory gửi thiếu trường bắt buộc là lỗi phụ thuộc (503), không báo khách là thiết bị ngừng cho thuê.
        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
        Assert.Empty(result.LineIssues);
    }

    [Fact]
    public async Task Prepare_ReturnsCurrentQuote_WhenPriceDiffersFromWhatRenterSaw()
    {
        _inventory.Listings[_listingId] = Listing(_listingId) with { DailyRate = 600_000m };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(3_000_000m));

        Assert.Equal(CheckoutErrors.PriceChanged, result.Error);
        Assert.NotNull(result.CurrentQuote);
        Assert.Equal(600_000m, result.CurrentQuote.Lines.Single().DailyRate);
        Assert.Equal(3_600_000m, result.CurrentQuote.TotalAmount);
    }

    [Fact]
    public async Task Prepare_ReturnsDependencyUnavailable_WhenInventoryListingReadFails()
    {
        _inventory.FailListingRead = true;

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
    }

    [Fact]
    public async Task Prepare_ReturnsDependencyUnavailable_WhenAvailabilityCheckFails()
    {
        _inventory.FailAvailabilityCheck = true;

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
    }

    [Fact]
    public async Task Prepare_CallsInventoryOnceForListingsAndOnceForAvailability_WholeCart()
    {
        Guid secondItem = AddCartItem(_listingId, 1, new(2026, 10, 20), new(2026, 10, 21));

        CheckoutPreparationResult result = await CreateService()
            .PrepareAsync(Renter(), Request(3_000_000m + 1_000_000m, _cartItemId, secondItem));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Plan.Lines.Count);
        // Một lần gọi cho Listing (hai dòng cùng Listing nên chỉ hỏi một mã); lịch thì mỗi dòng một lần.
        Assert.Equal(1, _inventory.ListingReadCount);
        Assert.Equal([_listingId], _inventory.RequestedListingIds);
        Assert.Equal(2, _inventory.AvailabilityCheckCount);
        Assert.Equal(
            [(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14)), (new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 21))],
            _inventory.AvailabilityPeriods);
    }

    [Fact]
    public async Task Prepare_WhenInventoryOmitsAListingFromBothResultsAndNotFound_ReportsDependencyUnavailable()
    {
        BrokenInventory inventory = new(_inventory) { DropSnapshots = true };

        CheckoutPreparationResult result = await CreateService(inventory: inventory).PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
    }

    [Fact]
    public async Task Prepare_WhenInventoryReturnsAListingThatWasNotAskedFor_ReportsDependencyUnavailable()
    {
        BrokenInventory inventory = new(_inventory) { ExtraSnapshot = Listing(Guid.NewGuid()) };

        CheckoutPreparationResult result = await CreateService(inventory: inventory).PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
    }

    [Fact]
    public async Task Prepare_WhenInventoryAnswersAvailabilityForOtherDates_ReportsDependencyUnavailable()
    {
        BrokenInventory inventory = new(_inventory) { ShiftAvailabilityEndDate = true };

        CheckoutPreparationResult result = await CreateService(inventory: inventory).PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
    }

    [Fact]
    public async Task Prepare_WhenListingDisappearsBeforeAvailabilityCheck_ReportsListingUnavailable()
    {
        BrokenInventory inventory = new(_inventory) { AvailabilityListingNotFound = true };

        CheckoutPreparationResult result = await CreateService(inventory: inventory).PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.ItemsUnavailable, result.Error);
        Assert.Equal(CheckoutLineIssueCode.ListingUnavailable, Assert.Single(result.LineIssues).Code);
    }

    /// <summary>Bọc Inventory giả để trả kết quả sai hợp đồng.</summary>
    private sealed class BrokenInventory(FakeInventoryListingClient inner) : IInventoryRentalClient
    {
        public bool DropSnapshots { get; init; }

        public ListingRentalSnapshotDto? ExtraSnapshot { get; init; }

        public bool ShiftAvailabilityEndDate { get; init; }

        public bool AvailabilityListingNotFound { get; init; }

        public async Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default)
        {
            Result<ListingRentalSnapshotsResponse> result = await inner.GetListingRentalSnapshotsAsync(request, ct);
            if (DropSnapshots)
            {
                return Result.Success(new ListingRentalSnapshotsResponse([], []));
            }

            return ExtraSnapshot is null
                ? result
                : Result.Success(result.Value with { Items = [.. result.Value.Items, ExtraSnapshot] });
        }

        public async Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default)
        {
            if (AvailabilityListingNotFound)
            {
                return Result.Failure<ListingAvailabilityCheckResponse>(
                    Error.NotFound(InventoryContractSpec.ErrorCodes.ListingNotFound, "Listing không tồn tại."));
            }

            Result<ListingAvailabilityCheckResponse> result = await inner.CheckListingAvailabilityAsync(request, ct);
            return ShiftAvailabilityEndDate
                ? Result.Success(result.Value with { EndDate = result.Value.EndDate.AddDays(1) })
                : result;
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

    [Fact]
    public async Task Prepare_BuyNow_BuildsOneLineWithoutReadingTheCart()
    {
        // Người thuê chưa có giỏ vẫn thuê ngay được.
        _carts.Cart = null;

        // 1 máy × 3 ngày (12, 13, 14) × 500.000 = 1.500.000, cọc 0.
        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), BuyNowRequest(1_500_000m));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Plan.CartId);
        CheckoutPlanLine line = Assert.Single(result.Plan.Lines);
        Assert.Equal(_listingId, line.EquipmentListingId);
        Assert.Equal(1, line.Quantity);
        Assert.NotEqual(_cartItemId, line.CartItemId);
        Assert.Equal(1_500_000m, result.Plan.TotalAmount);
    }

    [Fact]
    public async Task Prepare_BuyNow_StillRequiresBookingThreeDaysAhead()
    {
        // Bây giờ là 09/10: ngày giao sớm nhất là 12/10, nên 10/10 bị chặn.
        CheckoutPreparationResult result = await CreateService()
            .PrepareAsync(Renter(), BuyNowRequest(1_500_000m, start: new DateOnly(2026, 10, 10)));

        Assert.Equal(CheckoutErrors.InvalidRentalDates, result.Error);
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task Prepare_WithAddressId_UsesTheSavedAddressAndDeliveryNote()
    {
        RenterAddress site = new(Guid.NewGuid(), _renterId, "Bình Dương", "Công trường KCN VSIP 2, Thủ Dầu Một",
            "Trần Văn Bình", "0912345678", isDefault: true, Now);
        _addresses.Addresses.Add(site);

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(),
            Request() with { Delivery = null, AddressId = site.Id, DeliveryNote = " Gọi trước khi giao " });

        Assert.True(result.IsSuccess);
        Assert.Equal(new CheckoutDeliveryDto("Bình Dương", "Công trường KCN VSIP 2, Thủ Dầu Một",
            "Trần Văn Bình", "0912345678", "Gọi trước khi giao"), result.Plan.Delivery);
    }

    [Fact]
    public async Task Prepare_WithAnotherRentersAddressId_IsAddressNotFound()
    {
        RenterAddress strangers = new(Guid.NewGuid(), Guid.NewGuid(), "Đồng Nai", "Kho bãi số 5, Biên Hòa",
            "Lê Văn C", "0987654321", isDefault: true, Now);
        _addresses.Addresses.Add(strangers);

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(),
            Request() with { Delivery = null, AddressId = strangers.Id });

        Assert.Equal(RenterAddressErrors.NotFound, result.Error);
        Assert.Equal(0, _inventory.ListingReadCount);
    }
}
