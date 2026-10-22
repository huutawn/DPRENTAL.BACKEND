using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Application.Tests.Cart;

public sealed class RentalCartUseCasesTests
{
    private readonly Mock<IRentalCartRepository> _cartRepoMock = new();
    private readonly Mock<IRentalListingReader> _listingReaderMock = new();
    private readonly Mock<IRentalAvailabilityReader> _availabilityReaderMock = new();
    private readonly Mock<IRentalCartPolicy> _policyMock = new();
    private readonly Mock<IOrderLockManager> _lockManagerMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IRentalEventProducer> _eventProducerMock = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RentalCartItemEvaluator _evaluator;
    private readonly RentalCartUseCases _sut;

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();

    private readonly DateOnly _validStartDate;
    private readonly DateOnly _validEndDate;
    private readonly DateOnly _todayVn;

    public RentalCartUseCasesTests()
    {
        var nowUtc = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(nowUtc);
        _todayVn = new DateOnly(2026, 10, 15);
        _validStartDate = RentalBookingPolicy.EarliestStartDate(_todayVn);
        _validEndDate = _validStartDate.AddDays(4);

        _policyMock.Setup(x => x.MaximumRentalDays).Returns(365);

        _lockManagerMock
            .Setup(x => x.AcquireRenterLockAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IAsyncDisposable>().Object);

        var txMock = new Mock<IUnitOfWorkTransaction>();
        _unitOfWorkMock
            .Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(txMock.Object);

        _eventProducerMock
            .Setup(x => x.EnqueueCartItemAddedAsync(It.IsAny<CartItemAddedPayload>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "CartItemAdded", "topic", "key")));

        _evaluator = new RentalCartItemEvaluator(
            _listingReaderMock.Object,
            _availabilityReaderMock.Object,
            _policyMock.Object,
            _timeProvider);

        _sut = new RentalCartUseCases(
            _cartRepoMock.Object,
            _evaluator,
            _lockManagerMock.Object,
            _unitOfWorkMock.Object,
            _eventProducerMock.Object,
            _timeProvider);

        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _ownerId, _equipmentTypeId, true, true, 100m, 50m, "v1")));

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(10, true)));
    }

    // ==========================================
    // THÊM MỤC GIỎ HÀNG (AddCartItemAsync)
    // ==========================================

    [Fact]
    public async Task AddCartItem_Success_CreatesCartAndAddsItem_CallsAvailabilityOnce()
    {
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalCart?)null);

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 2, _validStartDate, _validEndDate);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Quantity);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 2, It.IsAny<CancellationToken>()), Times.Once);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Once);
        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.IsAny<CartItemAddedPayload>(), null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddCartItem_InsufficientAvailability_Returns409_DoesNotPersistOrEnqueueEvent()
    {
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalCart?)null);

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(3, false)));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 5, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.InsufficientAvailability", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Contains("3", result.Error.Message);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.IsAny<CartItemAddedPayload>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_ListingDisabled_Returns409ListingUnavailable()
    {
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _ownerId, _equipmentTypeId, false, false, 100m, 50m, "v1")));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_ListingNotFound_Returns409ListingUnavailable()
    {
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Listing không tồn tại"));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task AddCartItem_RenterIsOwner_Returns403CannotRentOwnListing()
    {
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _renterId, _equipmentTypeId, true, true, 100m, 50m, "v1")));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.CannotRentOwnListing, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task AddCartItem_StartDateLessThanThreeDaysAhead_Returns400StartDateInvalid()
    {
        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate.AddDays(-1), _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.StartDateInvalid", result.Error.Code);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_QuantityZeroOrNegative_Returns400()
    {
        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 0, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidQuantity, result.Error);
    }

    // ==========================================
    // THÊM TRÙNG DÒNG (Merge on Add)
    // ==========================================

    [Fact]
    public async Task AddCartItem_WhenExistingItem_ChecksTotalQuantity_CallsAvailabilityOnce_DoesNotEnqueueEvent()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Đang có 2, thêm 3 -> tổng số lượng đích là 5
        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 3, _validStartDate, _validEndDate);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(5, result.Value.Quantity);

        // Availability được gọi đúng 1 lần với tổng số lượng 5
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 5, It.IsAny<CancellationToken>()), Times.Once);

        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.IsAny<CartItemAddedPayload>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_WhenExistingItem_InsufficientStockForMerged_DoesNotIncreaseQuantity()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        // Đang có 2, thêm 3 -> cần 5 nhưng chỉ còn 4
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(4, false)));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 3, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.InsufficientAvailability", result.Error.Code);
        // Số lượng dòng cũ vẫn giữ nguyên là 2
        Assert.Equal(2, cart.Items.First().Quantity);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==========================================
    // ĐỔI SỐ LƯỢNG (UpdateCartItemQuantityAsync)
    // ==========================================

    [Fact]
    public async Task UpdateQuantity_Success_CallsAvailabilityOnceWithNewQuantity()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 1, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, cart.Items.First().Quantity);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateQuantity_InsufficientStock_KeepsOldQuantity()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 3, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        // Khách muốn sửa lên 5 nhưng chỉ còn 4
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(4, false)));

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 5);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.InsufficientAvailability", result.Error.Code);
        Assert.Equal(3, cart.Items.First().Quantity);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateQuantity_ListingDisabled_Returns409ListingUnavailable_DoesNotUpdate()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _ownerId, _equipmentTypeId, false, false, 100m, 50m, "v1")));

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 1);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(2, cart.Items.First().Quantity);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateQuantity_WhenDatePassed_AllowsSavingValidQuantity_WithoutCallingAvailability()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        // Ngày bắt đầu đã quá hạn đặt trước (chỉ cách hôm nay 1 ngày)
        DateOnly passedStart = _todayVn.AddDays(1);
        DateOnly passedEnd = passedStart.AddDays(3);
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 2, passedStart, passedEnd, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, cart.Items.First().Quantity);
        // Không gọi availability khi ngày đã quá hạn đặt trước!
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateQuantity_WhenDatePassedAndListingDisabled_BlocksUpdateWith409()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        DateOnly passedStart = _todayVn.AddDays(1);
        DateOnly passedEnd = passedStart.AddDays(3);
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 2, passedStart, passedEnd, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _ownerId, _equipmentTypeId, false, false, 100m, 50m, "v1")));

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 4);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(2, cart.Items.First().Quantity);
    }

    // ==========================================
    // ĐỔI NGÀY & GỘP DÒNG (UpdateCartItemDatesAsync)
    // ==========================================

    [Fact]
    public async Task UpdateDates_Success_NoDuplicate_UpdatesDatesAndCallsAvailability()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 3, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        DateOnly newStart = _validStartDate.AddDays(3);
        DateOnly newEnd = newStart.AddDays(4);

        var result = await _sut.UpdateCartItemDatesAsync(_renterId, cartItemId, newStart, newEnd);

        Assert.True(result.IsSuccess);
        var updated = cart.Items.First(i => i.Id == cartItemId);
        Assert.Equal(newStart, updated.ExpectedStartDate);
        Assert.Equal(newEnd, updated.ExpectedEndDate);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            _listingId, newStart, newEnd, 3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDates_InsufficientStock_KeepsOldDates()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 3, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        DateOnly newStart = _validStartDate.AddDays(3);
        DateOnly newEnd = newStart.AddDays(4);

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, newStart, newEnd, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(1, false)));

        var result = await _sut.UpdateCartItemDatesAsync(_renterId, cartItemId, newStart, newEnd);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.InsufficientAvailability", result.Error.Code);
        var item = cart.Items.First(i => i.Id == cartItemId);
        Assert.Equal(_validStartDate, item.ExpectedStartDate);
        Assert.Equal(_validEndDate, item.ExpectedEndDate);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateDates_DuplicateTarget_MergesIntoTarget_KeepsTargetId_DeletesSource()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid sourceItemId = Guid.NewGuid();
        Guid targetItemId = Guid.NewGuid();
        DateOnly targetStart = _validStartDate.AddDays(5);
        DateOnly targetEnd = targetStart.AddDays(3);

        // Dòng nguồn (2 máy)
        cart.AddItem(sourceItemId, _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());
        // Dòng đích đã có sẵn (3 máy)
        cart.AddItem(targetItemId, _listingId, _ownerId, _equipmentTypeId, 3, targetStart, targetEnd, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Đổi ngày dòng nguồn thành ngày của dòng đích -> tổng là 5 máy
        var result = await _sut.UpdateCartItemDatesAsync(_renterId, sourceItemId, targetStart, targetEnd);

        Assert.True(result.IsSuccess);
        // Kiểm tra availability gọi đúng 1 lần với tổng số lượng 5
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            _listingId, targetStart, targetEnd, 5, It.IsAny<CancellationToken>()), Times.Once);

        // Chỉ còn 1 item trong giỏ, giữ nguyên ID của dòng đích, số lượng 5
        var singleItem = Assert.Single(cart.Items);
        Assert.Equal(targetItemId, singleItem.Id);
        Assert.Equal(5, singleItem.Quantity);
    }

    [Fact]
    public async Task UpdateDates_DuplicateTarget_InsufficientStock_KeepsBothLinesIntact()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid sourceItemId = Guid.NewGuid();
        Guid targetItemId = Guid.NewGuid();
        DateOnly targetStart = _validStartDate.AddDays(5);
        DateOnly targetEnd = targetStart.AddDays(3);

        cart.AddItem(sourceItemId, _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());
        cart.AddItem(targetItemId, _listingId, _ownerId, _equipmentTypeId, 3, targetStart, targetEnd, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        // Tổng là 5 máy nhưng chỉ còn 4
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, targetStart, targetEnd, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalAvailabilityResult(4, false)));

        var result = await _sut.UpdateCartItemDatesAsync(_renterId, sourceItemId, targetStart, targetEnd);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.InsufficientAvailability", result.Error.Code);
        // Cả hai dòng vẫn còn nguyên vẹn
        Assert.Equal(2, cart.Items.Count);
        Assert.Equal(2, cart.Items.First(i => i.Id == sourceItemId).Quantity);
        Assert.Equal(3, cart.Items.First(i => i.Id == targetItemId).Quantity);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==========================================
    // XÓA ITEM VÀ CLEAR CART (Không phụ thuộc Inventory)
    // ==========================================

    [Fact]
    public async Task RemoveCartItem_DoesNotDependOnInventoryAvailability()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = cart.AddItem(Guid.NewGuid(), _listingId, _ownerId, 1,
            _validStartDate, _validEndDate, _timeProvider.GetUtcNow()).Value.Id;
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _listingReaderMock.Setup(x => x.GetListingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Inventory unavailable."));

        Result result = await _sut.RemoveCartItemAsync(_renterId, cartItemId);

        Assert.True(result.IsSuccess);
        Assert.Empty(cart.Items);
        _listingReaderMock.Verify(x => x.GetListingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClearCart_DoesNotDependOnInventoryAvailability()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, _ownerId, 1,
            _validStartDate, _validEndDate, _timeProvider.GetUtcNow());
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _listingReaderMock.Setup(x => x.GetListingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Inventory unavailable."));

        Result result = await _sut.ClearCartAsync(_renterId);

        Assert.True(result.IsSuccess);
        Assert.Empty(cart.Items);
        _listingReaderMock.Verify(x => x.GetListingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==========================================
    // PATCH VALIDATION
    // ==========================================

    [Fact]
    public async Task UpdateCartItem_MissingAllFields_ReturnsInvalidCartUpdate()
    {
        var request = new UpdateCartItemRequest(null, null, null);
        var result = await _sut.UpdateCartItemAsync(_renterId, Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidCartUpdate, result.Error);
    }

    [Fact]
    public async Task UpdateCartItem_BothQuantityAndDates_ReturnsInvalidCartUpdate()
    {
        var request = new UpdateCartItemRequest(5, _validStartDate, _validEndDate);
        var result = await _sut.UpdateCartItemAsync(_renterId, Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidCartUpdate, result.Error);
    }

    [Fact]
    public async Task RemoveCartItem_RepositoryUpdateFails_ReturnsConflict()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, 1, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.RemoveCartItemAsync(_renterId, cartItemId);

        Assert.True(result.IsFailure);
        Assert.Equal("CONCURRENCY_CONFLICT", result.Error.Code);
    }

    [Fact]
    public async Task AddCartItem_RaceCondition_ListingDeletedDuringAvailability_Returns409ListingUnavailable()
    {
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalCart?)null);

        // Snapshot thành công nhưng availability trả NotFound
        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Listing deleted"));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.IsAny<CartItemAddedPayload>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateQuantity_RaceCondition_ListingDeletedDuringAvailability_Returns409ListingUnavailable()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, _validStartDate, _validEndDate, 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Listing deleted"));

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 4);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(2, cart.Items.First().Quantity);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateDates_RaceCondition_ListingDeletedDuringAvailability_Returns409ListingUnavailable()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        DateOnly newStart = _validStartDate.AddDays(3);
        DateOnly newEnd = newStart.AddDays(4);

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, newStart, newEnd, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Listing deleted"));

        var result = await _sut.UpdateCartItemDatesAsync(_renterId, cartItemId, newStart, newEnd);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        var item = cart.Items.First(i => i.Id == cartItemId);
        Assert.Equal(_validStartDate, item.ExpectedStartDate);
        Assert.Equal(_validEndDate, item.ExpectedEndDate);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateDates_DuplicateTarget_RaceCondition_ListingDeletedDuringAvailability_KeepsBothLinesIntact()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid sourceItemId = Guid.NewGuid();
        Guid targetItemId = Guid.NewGuid();
        DateOnly targetStart = _validStartDate.AddDays(5);
        DateOnly targetEnd = targetStart.AddDays(3);

        cart.AddItem(sourceItemId, _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());
        cart.AddItem(targetItemId, _listingId, _ownerId, _equipmentTypeId, 3, targetStart, targetEnd, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        _availabilityReaderMock.Setup(x => x.GetAvailableQuantityAsync(
            _listingId, targetStart, targetEnd, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.NotFound("NOT_FOUND", "Listing deleted"));

        var result = await _sut.UpdateCartItemDatesAsync(_renterId, sourceItemId, targetStart, targetEnd);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.ListingUnavailable", result.Error.Code);
        Assert.Equal(2, cart.Items.Count);
        Assert.Equal(2, cart.Items.First(i => i.Id == sourceItemId).Quantity);
        Assert.Equal(3, cart.Items.First(i => i.Id == targetItemId).Quantity);
        _cartRepoMock.Verify(x => x.UpdateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateQuantity_WhenPeriodInvertedOrTooLong_DoesNotGetDatePassedException()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        DateOnly start = _todayVn.AddDays(1); // Quá hạn đặt trước (chỉ 1 ngày)
        DateOnly end = start.AddDays(5); // 6 ngày thuê
        cart.AddItem(cartItemId, _listingId, _ownerId, _equipmentTypeId, 2, start, end, _timeProvider.GetUtcNow());

        _policyMock.Setup(x => x.MaximumRentalDays).Returns(4); // Policy giới hạn 4 ngày

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        var result = await _sut.UpdateCartItemQuantityAsync(_renterId, cartItemId, 4);

        // Bị từ chối vì kỳ thuê vượt quá policy (400), không được hưởng ngoại lệ DatePassed!
        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.DurationExceedsOneYear.Code, result.Error.Code);
        Assert.Equal(2, cart.Items.First().Quantity);
        _availabilityReaderMock.Verify(x => x.GetAvailableQuantityAsync(
            It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
