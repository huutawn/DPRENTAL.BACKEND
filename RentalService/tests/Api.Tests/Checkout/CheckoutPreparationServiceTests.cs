using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Checkout;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Options;
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
    // Khoảng thuê [12/10, 15/10) là 3 ngày: 12, 13, 14.
    private static readonly DateOnly End = new(2026, 10, 15);

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();
    private readonly Guid _brandId = Guid.NewGuid();
    private readonly Guid _thumbnailMediaId = Guid.NewGuid();

    private readonly FakeCartRepository _carts = new();
    private readonly FakeOrderRepository _orders = new();
    private readonly FakeInventoryListingClient _inventory = new();
    private readonly FakeCatalogReferenceClient _catalog = new();
    private readonly RentalCart _cart;
    private readonly Guid _cartItemId;

    public CheckoutPreparationServiceTests()
    {
        _cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        _cartItemId = AddCartItem(_listingId, 2, Start, End);
        _carts.Cart = _cart;

        _inventory.Listings[_listingId] = Listing(_listingId);
        _inventory.AvailableQuantities[_listingId] = 10;
        _catalog.EquipmentTypeNames[_equipmentTypeId] = "Máy ảnh";
        _catalog.BrandNames[_brandId] = "Sony";
    }

    private Guid AddCartItem(Guid listingId, int quantity, DateOnly start, DateOnly end) =>
        _cart.AddItem(Guid.NewGuid(), listingId, _supplierId, quantity, start, end, Now).Value.Id;

    private InventoryListingDto Listing(Guid id) => new(
        id,
        _supplierId,
        true,
        " Sony A7 IV ",
        _equipmentTypeId,
        _brandId,
        "A7 IV",
        3,
        """{"sensor":"full-frame"}""",
        _thumbnailMediaId,
        500_000m,
        2_000_000m,
        "VND",
        "Quận 1, TP.HCM");

    private CheckoutRequest Request(decimal expectedTotal = 7_000_000m, params Guid[] cartItemIds) => new(
        cartItemIds.Length == 0 ? [_cartItemId] : cartItemIds,
        new(" Hồ Chí Minh ", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", "  "),
        new("VCB", "0123456789", "NGUYEN VAN A"),
        "v1.0",
        true,
        expectedTotal);

    private CheckoutRenterContext Renter(bool profileCompleted = true) => new(_renterId, profileCompleted);

    private CheckoutPreparationService CreateService(DateTimeOffset? utcNow = null) => new(
        _carts,
        _orders,
        _inventory,
        _catalog,
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
        Assert.Equal("Quận 1, TP.HCM", line.PickupLocation);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(3, line.RentalDays);
        Assert.Equal(500_000m, line.DailyRate);
        Assert.Equal(2_000_000m, line.DepositPerUnit);
        Assert.Equal(3_000_000m, line.RentalAmount);
        Assert.Equal(4_000_000m, line.DepositAmount);
        Assert.Equal(
            new RentalItemListingSnapshotDto(
                _equipmentTypeId, "Máy ảnh", _brandId, "Sony", "A7 IV", 3, """{"sensor":"full-frame"}""", _thumbnailMediaId),
            line.ListingSnapshot);

        Assert.Equal(3_000_000m, plan.TotalRentalAmount);
        Assert.Equal(4_000_000m, plan.TotalDepositAmount);
        Assert.Equal(7_000_000m, plan.TotalAmount);
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
                    line.PickupLocation,
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
        Guid oneDayItem = AddCartItem(_listingId, 1, new(2026, 10, 20), new(2026, 10, 21));

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(2_500_000m, oneDayItem));

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
        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(7_000_000m, Guid.NewGuid()));

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
    public async Task Prepare_AcceptsStartDateThatIsTomorrowInVietnamTime()
    {
        // 16:30 UTC ngày 11/10 là 23:30 ngày 11/10 giờ Việt Nam, nên ngày giao 12/10 là "ngày mai".
        DateTimeOffset beforeMidnightIct = new(2026, 10, 11, 16, 30, 0, TimeSpan.Zero);

        CheckoutPreparationResult result = await CreateService(beforeMidnightIct).PrepareAsync(Renter(), Request());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Prepare_RejectsRentalLongerThan30Days_AndStartBeyondSixMonths()
    {
        Guid tooLong = AddCartItem(_listingId, 1, new(2026, 11, 1), new(2026, 12, 2));
        Guid thirtyDays = AddCartItem(_listingId, 1, new(2026, 11, 1), new(2026, 12, 1));
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
        _inventory.Listings[inactiveListing] = Listing(inactiveListing) with { IsActive = false };
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
    [InlineData(null, 2_000_000, "VND")]
    [InlineData(0, 2_000_000, "VND")]
    [InlineData(500_000, null, "VND")]
    [InlineData(500_000, -1, "VND")]
    [InlineData(500_000, 2_000_000, "USD")]
    [InlineData(500_000, 2_000_000, null)]
    public async Task Prepare_RejectsListingWithoutUsablePricing(int? dailyRate, int? deposit, string? currency)
    {
        _inventory.Listings[_listingId] = Listing(_listingId) with
        {
            DailyRate = dailyRate,
            DepositPerUnit = deposit,
            Currency = currency
        };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.ItemsUnavailable, result.Error);
        Assert.Equal(CheckoutLineIssueCode.ListingUnavailable, Assert.Single(result.LineIssues).Code);
    }

    [Fact]
    public async Task Prepare_AcceptsZeroDeposit()
    {
        _inventory.Listings[_listingId] = Listing(_listingId) with { DepositPerUnit = 0m };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(3_000_000m));

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Plan.TotalDepositAmount);
    }

    [Fact]
    public async Task Prepare_RejectsListingWhoseBrandIsNoLongerPublicInCatalog()
    {
        _catalog.BrandNames.Clear();

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.ItemsUnavailable, result.Error);
        Assert.Equal(CheckoutLineIssueCode.ListingUnavailable, Assert.Single(result.LineIssues).Code);
    }

    [Fact]
    public async Task Prepare_ReturnsCurrentQuote_WhenPriceDiffersFromWhatRenterSaw()
    {
        _inventory.Listings[_listingId] = Listing(_listingId) with { DailyRate = 600_000m };

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request(7_000_000m));

        Assert.Equal(CheckoutErrors.PriceChanged, result.Error);
        Assert.NotNull(result.CurrentQuote);
        Assert.Equal(600_000m, result.CurrentQuote.Lines.Single().DailyRate);
        Assert.Equal(3_600_000m + 4_000_000m, result.CurrentQuote.TotalAmount);
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
    public async Task Prepare_ReturnsDependencyUnavailable_WhenCatalogFails()
    {
        _catalog.Fail = true;

        CheckoutPreparationResult result = await CreateService().PrepareAsync(Renter(), Request());

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
    }

    [Fact]
    public async Task Prepare_ReadsEachListingAndCatalogEntryOnce_ForLinesOfTheSameListing()
    {
        Guid secondItem = AddCartItem(_listingId, 1, new(2026, 10, 20), new(2026, 10, 22));

        CheckoutPreparationResult result = await CreateService()
            .PrepareAsync(Renter(), Request(7_000_000m + 3_000_000m, _cartItemId, secondItem));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Plan.Lines.Count);
        Assert.Equal(1, _inventory.ListingReadCount);
        Assert.Equal(2, _inventory.AvailabilityCheckCount);
        Assert.Equal(2, _catalog.CallCount);
    }
}
