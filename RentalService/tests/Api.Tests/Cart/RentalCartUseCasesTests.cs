using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Services.Cart;
using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Application.Tests.Cart; // Sửa lại namespace theo đúng vị trí thư mục của bạn nếu cần

public sealed class RentalCartUseCasesTests
{
    private readonly Mock<IRentalCartRepository> _cartRepoMock = new();
    private readonly Mock<IRentalListingOwnerProvider> _ownerProviderMock = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RentalCartUseCases _sut;

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private readonly DateOnly _validStartDate;
    private readonly DateOnly _validEndDate;

    public RentalCartUseCasesTests()
    {
        var nowUtc = DateTimeOffset.UtcNow;

        _timeProvider.SetUtcNow(nowUtc);

        _sut = new RentalCartUseCases(_cartRepoMock.Object, _ownerProviderMock.Object, _timeProvider);

        var todayVn = DateOnly.FromDateTime(nowUtc.AddHours(7).DateTime);

        _validStartDate = todayVn.AddDays(1); // Ngày hợp lệ là ngày mai
        _validEndDate = todayVn.AddDays(5);   // Ngày kết thúc hợp lệ là 3 ngày sau
    }

    [Fact]
    public async Task AddCartItem_NoActiveCart_CreatesCartAndAddsItem()
    {
        _ownerProviderMock.Setup(x => x.GetOwnerIdAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_ownerId);

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
        _ownerProviderMock.Setup(x => x.GetOwnerIdAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_renterId);

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, _validStartDate, _validEndDate);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.CannotRentOwnListing, result.Error);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveCartItem_RepositoryUpdateFails_ReturnsCartNotFound()
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
        Assert.Equal(RentalErrors.CartNotFound, result.Error);
    }
}