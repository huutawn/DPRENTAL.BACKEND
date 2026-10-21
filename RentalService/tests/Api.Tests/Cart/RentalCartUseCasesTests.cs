using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Application.Tests.Cart; // Sửa lại namespace theo đúng vị trí thư mục của bạn nếu cần

public sealed class RentalCartUseCasesTests
{
    private readonly Mock<IRentalCartRepository> _cartRepoMock = new();
    private readonly Mock<IRentalListingReader> _listingReaderMock = new();
    private readonly Mock<IOrderLockManager> _lockManagerMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IRentalEventProducer> _eventProducerMock = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RentalCartUseCases _sut;

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();

    private readonly DateOnly _validStartDate;
    private readonly DateOnly _validEndDate;

    public RentalCartUseCasesTests()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        _timeProvider.SetUtcNow(nowUtc);

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


        _sut = new RentalCartUseCases(
            _cartRepoMock.Object,
            _listingReaderMock.Object,
            _lockManagerMock.Object,
            _unitOfWorkMock.Object,
            _eventProducerMock.Object,
            _timeProvider);

        var todayVn = DateOnly.FromDateTime(nowUtc.AddHours(7).DateTime);
        _validStartDate = RentalBookingPolicy.EarliestStartDate(todayVn);
        _validEndDate = todayVn.AddDays(5);

        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _ownerId, _equipmentTypeId, true, true, 100m, 50m, "v1")));
    }

    [Fact]
    public async Task AddCartItem_StartDateLessThanThreeDaysAhead_ReturnsStartDateInvalid()
    {


        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate.AddDays(-1), _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("Cart.StartDateInvalid", result.Error.Code);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_NoActiveCart_CreatesCartAndAddsItem()
    {


        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalCart?)null);

        // Sử dụng _validStartDate thay vì ngày fix cứng cũ
        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsSuccess, result.Error?.Message);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddCartItem_RenterIsOwner_ReturnsCannotRentOwnListing()
    {
        // Setup Provider trả về OwnerId trùng với RenterId
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _renterId, _equipmentTypeId, true, true, 100m, 50m, "v1")));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.CannotRentOwnListing, result.Error);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveCartItem_RepositoryUpdateFails_ReturnsConflict()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();

        // Thêm sẵn 1 item hợp lệ vào giỏ
        cart.AddItem(cartItemId, _listingId, _ownerId, 1, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        // Setup UpdateAsync trả về false (giả lập lỗi version concurrency)
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.RemoveCartItemAsync(_renterId, cartItemId);

        Assert.True(result.IsFailure);
        Assert.Equal("CONCURRENCY_CONFLICT", result.Error.Code);
    }

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

    [Fact]
    public async Task UpdateCartItem_MissingAllFields_ReturnsInvalidCartUpdate()
    {
        var request = new UpdateCartItemRequest(null, null, null);
        var result = await _sut.UpdateCartItemAsync(_renterId, Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidCartUpdate, result.Error);
    }

    [Fact]
    public async Task UpdateCartItem_OnlyStartDate_ReturnsInvalidCartUpdate()
    {
        var request = new UpdateCartItemRequest(null, _validStartDate, null);
        var result = await _sut.UpdateCartItemAsync(_renterId, Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidCartUpdate, result.Error);
    }

    [Fact]
    public async Task UpdateCartItem_OnlyEndDate_ReturnsInvalidCartUpdate()
    {
        var request = new UpdateCartItemRequest(null, null, _validEndDate);
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
    public async Task UpdateCartItem_ValidQuantity_DispatchesToQuantityUpdate()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, 1, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var request = new UpdateCartItemRequest(3, null, null);
        var result = await _sut.UpdateCartItemAsync(_renterId, cartItemId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, cart.Items.First(i => i.Id == cartItemId).Quantity);
    }

    [Fact]
    public async Task UpdateCartItem_ValidDates_DispatchesToDatesUpdate()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        Guid cartItemId = Guid.NewGuid();
        cart.AddItem(cartItemId, _listingId, _ownerId, 1, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        DateOnly newStart = _validStartDate.AddDays(2);
        DateOnly newEnd = _validEndDate.AddDays(2);
        var request = new UpdateCartItemRequest(null, newStart, newEnd);
        var result = await _sut.UpdateCartItemAsync(_renterId, cartItemId, request);

        Assert.True(result.IsSuccess);
        var updated = cart.Items.First(i => i.Id == cartItemId);
        Assert.Equal(newStart, updated.ExpectedStartDate);
        Assert.Equal(newEnd, updated.ExpectedEndDate);
    }

    [Fact]
    public async Task AddCartItem_WhenNewItem_EnqueuesCartItemAdded_AndCommits()
    {
        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalCart?)null);

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsSuccess, result.Error?.Message);
        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.Is<CartItemAddedPayload>(p =>
                p.UserId == _renterId &&
                p.EquipmentListingId == _listingId &&
                p.EquipmentTypeId == _equipmentTypeId &&
                p.CartItemId == result.Value.Id),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddCartItem_WhenExistingItem_IncreasesQuantity_DoesNotEnqueueEvent()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, _timeProvider.GetUtcNow()).Value;
        cart.AddItem(Guid.NewGuid(), _listingId, _ownerId, _equipmentTypeId, 2, _validStartDate, _validEndDate, _timeProvider.GetUtcNow());

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 3, _validStartDate, _validEndDate);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(5, result.Value.Quantity);
        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.IsAny<CartItemAddedPayload>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_WhenMissingEquipmentTypeId_ReturnsError_DoesNotPersist()
    {
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RentalListingSnapshot(_listingId, _ownerId, Guid.Empty, true, true, 100m, 50m, "v1")));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_EQUIPMENT_TYPE_ID", result.Error.Code);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventProducerMock.Verify(x => x.EnqueueCartItemAddedAsync(
            It.IsAny<CartItemAddedPayload>(), null, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCartItem_WhenInventoryFailsWith503_ReturnsServiceUnavailable()
    {
        _listingReaderMock.Setup(x => x.GetListingAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Unavailable"));

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
    }
}
