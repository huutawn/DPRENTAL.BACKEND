using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Application.Tests.Cart;

public sealed class GetRentalCartTests
{
    private readonly Mock<IRentalCartRepository> _cartRepoMock = new();
    private readonly Mock<IRentalListingReader> _listingReaderMock = new();
    private readonly Mock<IRentalAvailabilityReader> _availabilityReaderMock = new();
    private readonly Mock<IRentalCartPolicy> _policyMock = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly GetRentalCart _sut;

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly DateOnly _todayVn;

    public GetRentalCartTests()
    {
        // Setup chính sách mặc định là 365 ngày
        _policyMock.Setup(x => x.MaximumRentalDays).Returns(365);

        // BỔ SUNG _policyMock.Object vào hàm khởi tạo
        _sut = new GetRentalCart(_cartRepoMock.Object, _listingReaderMock.Object, _availabilityReaderMock.Object, _policyMock.Object, _timeProvider);

        // Thiết lập đồng hồ giả lập (0:00 UTC = 7:00 sáng tại VN, ngày 15/10/2026)
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        _todayVn = new DateOnly(2026, 10, 15);
    }

    private RentalCart SetupCartWithOneItem(DateOnly start, DateOnly end, int qty = 1)
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, Guid.NewGuid(), qty, start, end, _timeProvider.GetUtcNow());
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, default)).ReturnsAsync(cart);
        return cart;
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCart_ReturnsEmptyLines()
    {
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, default)).ReturnsAsync((RentalCart?)null);
        var result = await _sut.ExecuteAsync(_renterId, null);
        Assert.Empty(result.Value.Items);
    }

    [Fact]
    public async Task ExecuteAsync_DatePassed_ReturnsDatePassedStatus()
    {
        SetupCartWithOneItem(_todayVn, _todayVn.AddDays(1)); // Start là hôm nay (đã qua)

        var result = await _sut.ExecuteAsync(_renterId, null);

        var item = result.Value.Items.Single();
        Assert.Equal(RentalCartLineStatus.DatePassed, item.Status);
    }

    [Fact]
    public async Task ExecuteAsync_StartDateLessThanThreeDaysAhead_ReturnsDatePassedStatus()
    {
        // Hôm nay 15/10: ngày 17/10 chỉ còn 2 ngày, sớm nhất phải là 18/10.
        SetupCartWithOneItem(_todayVn.AddDays(2), _todayVn.AddDays(4));

        var result = await _sut.ExecuteAsync(_renterId, null);

        var item = result.Value.Items.Single();
        Assert.Equal(RentalCartLineStatus.DatePassed, item.Status);
        Assert.Contains("18/10/2026", item.Message);
        _listingReaderMock.Verify(x => x.GetListingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OneDayRental_CalculatesCorrectAmount()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        var cart = SetupCartWithOneItem(start, start, qty: 2); // Cùng 1 ngày, số lượng 2
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), true, true, 100m, 50m, "v1");

        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(listing);
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start, default)).ReturnsAsync(10);

        var result = await _sut.ExecuteAsync(_renterId, null);

        var item = result.Value.Items.Single();
        Assert.Equal(RentalCartLineStatus.Available, item.Status);
        Assert.Equal(100m * 1 * 2, item.EstimatedRentalAmount); // Rate * 1 ngày * số lượng
    }

    [Fact]
    public async Task ExecuteAsync_DurationExceedsConfiguredMaximum_ReturnsValidationFailed()
    {
        _policyMock.Setup(x => x.MaximumRentalDays).Returns(4);
        SetupCartWithOneItem(_todayVn.AddDays(1), _todayVn.AddDays(5)); // 5 inclusive days; policy allows 4
        var result = await _sut.ExecuteAsync(_renterId, null);
        Assert.Equal(RentalCartLineStatus.ValidationFailed, result.Value.Items.Single().Status);
    }

    [Fact]
    public async Task ExecuteAsync_PriceVersionMismatched_ReturnsPriceChanged()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        var cart = SetupCartWithOneItem(start, start.AddDays(2));
        var cartItemId = cart.Items.Single().Id;

        // Listing trên DB đang là "v2"
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), true, true, 100m, 50m, "v2");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(listing);

        // Client truyền lên "v1"
        var clientVersions = new Dictionary<Guid, string> { { cartItemId, "v1" } };

        var result = await _sut.ExecuteAsync(_renterId, clientVersions);

        Assert.Equal(RentalCartLineStatus.PriceChanged, result.Value.Items.Single().Status);
    }

    [Fact]
    public async Task ExecuteAsync_ListingOwnerIsRenter_ReturnsValidationFailed()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2));

        // Owner của Listing lại chính là người đang thuê (_renterId)
        var listing = new RentalListingSnapshot(_listingId, _renterId, true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(listing);

        var result = await _sut.ExecuteAsync(_renterId, null);
        Assert.Equal(RentalCartLineStatus.ValidationFailed, result.Value.Items.Single().Status);
    }

    [Fact]
    public async Task ExecuteAsync_InsufficientQuantity_ReturnsInsufficientQuantityStatus()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2), qty: 5); // Khách muốn thuê 5

        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(listing);

        // Hệ thống inventory báo chỉ còn 3
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start.AddDays(2), default))
            .ReturnsAsync(3);

        var result = await _sut.ExecuteAsync(_renterId, null);
        Assert.Equal(RentalCartLineStatus.InsufficientQuantity, result.Value.Items.Single().Status);
    }
}
