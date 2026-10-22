using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
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
    private readonly RentalCartItemEvaluator _evaluator;
    private readonly GetRentalCart _sut;

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly DateOnly _todayVn;

    public GetRentalCartTests()
    {
        _policyMock.Setup(x => x.MaximumRentalDays).Returns(365);
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        _todayVn = new DateOnly(2026, 10, 15);

        _evaluator = new RentalCartItemEvaluator(
            _listingReaderMock.Object,
            _availabilityReaderMock.Object,
            _policyMock.Object,
            _timeProvider);

        _sut = new GetRentalCart(_cartRepoMock.Object, _evaluator);
    }

    private RentalCart SetupCartWithOneItem(DateOnly start, DateOnly end, int qty = 1)
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, Guid.NewGuid(), Guid.NewGuid(), qty, start, end, _timeProvider.GetUtcNow());
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, default)).ReturnsAsync(cart);
        return cart;
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCart_ReturnsEmptyLines()
    {
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, default)).ReturnsAsync((RentalCart?)null);
        var result = await _sut.ExecuteAsync(_renterId);
        Assert.Empty(result.Value.Items);
    }

    [Fact]
    public async Task ExecuteAsync_ListingNotFound_ReturnsUnavailableAndDisabledReadOnly()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2));
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Sản phẩm không tồn tại"));

        var result = await _sut.ExecuteAsync(_renterId);

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.Unavailable, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledReadOnly, item.InteractionStatus);
        Assert.Null(item.CurrentDailyRate);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ListingDisabled_TakesPriorityOverDatePassed()
    {
        // Item đã quá ngày nhưng listing bị vô hiệu hóa -> phải ưu tiên ListingDisabled, DisabledReadOnly!
        SetupCartWithOneItem(_todayVn, _todayVn.AddDays(1));
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), false, false, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));

        var result = await _sut.ExecuteAsync(_renterId);

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.ListingDisabled, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledReadOnly, item.InteractionStatus);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_DatePassed_ReturnsDatePassedAndDisabledEditable()
    {
        // Listing còn hoạt động nhưng ngày đã qua -> DatePassed, DisabledEditable, không gọi availability
        SetupCartWithOneItem(_todayVn, _todayVn.AddDays(1));
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));

        var result = await _sut.ExecuteAsync(_renterId);

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.DatePassed, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledEditable, item.InteractionStatus);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StartDateLessThanThreeDaysAhead_ReturnsDatePassedStatus()
    {
        // Hôm nay 15/10: ngày 17/10 chỉ còn 2 ngày, sớm nhất phải là 18/10.
        SetupCartWithOneItem(_todayVn.AddDays(2), _todayVn.AddDays(4));
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));

        var result = await _sut.ExecuteAsync(_renterId);

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.DatePassed, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledEditable, item.InteractionStatus);
        Assert.Contains("18/10/2026", item.Message);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OneDayRental_CalculatesCorrectAmount()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        var cart = SetupCartWithOneItem(start, start, qty: 2);
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");

        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start, 2, default))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(10, true)));

        var result = await _sut.ExecuteAsync(_renterId);

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.Available, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.Enabled, item.InteractionStatus);
        Assert.Equal(100m * 1 * 2, item.EstimatedRentalAmount);
    }

    [Fact]
    public async Task ExecuteAsync_DurationExceedsConfiguredMaximum_ReturnsValidationFailed()
    {
        _policyMock.Setup(x => x.MaximumRentalDays).Returns(4);
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(5));
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));

        var result = await _sut.ExecuteAsync(_renterId);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.ValidationFailed, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledEditable, item.InteractionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsLatestPriceFromInventory()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2));
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v2");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start.AddDays(2), 1, default))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(10, true)));

        var result = await _sut.ExecuteAsync(_renterId);

        var line = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.Available, line.Status);
        Assert.Equal(RentalCartItemInteractionStatus.Enabled, line.InteractionStatus);
        Assert.Equal("v2", line.CurrentPriceVersion);
    }

    [Fact]
    public async Task ExecuteAsync_ListingOwnerIsRenter_ReturnsValidationFailedAndDisabledReadOnly()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2));

        var listing = new RentalListingSnapshot(_listingId, _renterId, Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));

        var result = await _sut.ExecuteAsync(_renterId);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.ValidationFailed, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledReadOnly, item.InteractionStatus);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InsufficientQuantity_ReturnsInsufficientQuantityStatusAndDisabledEditable()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2), qty: 5);

        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start.AddDays(2), 5, default))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(3, false)));

        var result = await _sut.ExecuteAsync(_renterId);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(RentalCartLineStatus.InsufficientQuantity, item.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledEditable, item.InteractionStatus);
        Assert.Equal("Chỉ còn 3 sản phẩm khả dụng.", item.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenListingLookupFailsWith503_ReturnsServiceUnavailable()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2));
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default))
            .ReturnsAsync(Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Unavailable"));
        var result = await _sut.ExecuteAsync(_renterId);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAvailabilityLookupFailsWith503_ReturnsServiceUnavailable()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        SetupCartWithOneItem(start, start.AddDays(2));
        var listing = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing));
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start.AddDays(2), 1, default))
            .ReturnsAsync(Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Unavailable"));

        var result = await _sut.ExecuteAsync(_renterId);

        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
    }

    [Fact]
    public async Task ExecuteAsync_RaceCondition_ListingDeletedDuringAvailabilityCheck_ReturnsUnavailableAndDisabledReadOnly()
    {
        var start = RentalBookingPolicy.EarliestStartDate(_todayVn);
        Guid listing2Id = Guid.NewGuid();

        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, Guid.NewGuid(), Guid.NewGuid(), 1, start, start.AddDays(2), _timeProvider.GetUtcNow());
        cart.AddItem(Guid.NewGuid(), listing2Id, Guid.NewGuid(), Guid.NewGuid(), 1, start, start.AddDays(2), _timeProvider.GetUtcNow());
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, default)).ReturnsAsync(cart);

        // Item 1: snapshot thành công, nhưng availability trả NotFound (vừa bị xóa)
        var listing1 = new RentalListingSnapshot(_listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1");
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, default)).ReturnsAsync(Result.Success(listing1));
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(_listingId, start, start.AddDays(2), 1, default))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Listing không còn tồn tại"));

        // Item 2: hoàn toàn bình thường
        var listing2 = new RentalListingSnapshot(listing2Id, Guid.NewGuid(), Guid.NewGuid(), true, true, 200m, 80m, "v2");
        _listingReaderMock.Setup(x => x.GetListingAsync(listing2Id, default)).ReturnsAsync(Result.Success(listing2));
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(listing2Id, start, start.AddDays(2), 1, default))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(10, true)));

        var result = await _sut.ExecuteAsync(_renterId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Items.Count);

        var line1 = result.Value.Items.First(i => i.EquipmentListingId == _listingId);
        Assert.Equal(RentalCartLineStatus.Unavailable, line1.Status);
        Assert.Equal(RentalCartItemInteractionStatus.DisabledReadOnly, line1.InteractionStatus);
        Assert.Null(line1.CurrentDailyRate);
        Assert.Null(line1.EstimatedRentalAmount);

        var line2 = result.Value.Items.First(i => i.EquipmentListingId == listing2Id);
        Assert.Equal(RentalCartLineStatus.Available, line2.Status);
        Assert.Equal(RentalCartItemInteractionStatus.Enabled, line2.InteractionStatus);
        Assert.Equal(200m * 3, line2.EstimatedRentalAmount);

        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(_listingId, start, start.AddDays(2), 1, default), Times.Once);
        _listingReaderMock.Verify(x => x.GetListingAsync(_listingId, default), Times.Once);
    }
}
