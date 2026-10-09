using Dprental.RentalService.Application.Cart;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Xunit;

namespace RentalService.Api.Tests.Cart;

// Đã thêm 'sealed' để fix lỗi CA1852 và chuyển thành public
public sealed class RentalCartUseCasesTests
{
    private readonly Mock<IRentalCartRepository> _cartRepoMock = new();
    private readonly Mock<IRentalListingOwnerProvider> _ownerProviderMock = new();
    private readonly RentalCartUseCases _sut;
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    public RentalCartUseCasesTests()
    {
        _sut = new RentalCartUseCases(_cartRepoMock.Object, _ownerProviderMock.Object, TimeProvider.System);
    }

    [Fact]
    public async Task AddCartItem_NoActiveCart_CreatesCartAndAddsItem()
    {
        _ownerProviderMock.Setup(x => x.GetOwnerIdAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_ownerId);

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalCart?)null);

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 5));

        Assert.True(result.IsSuccess);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddCartItem_RenterIsOwner_ReturnsCannotRentOwnListing()
    {
        // Setup Provider trả về OwnerId trùng với RenterId
        _ownerProviderMock.Setup(x => x.GetOwnerIdAsync(_listingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_renterId);

        var result = await _sut.AddCartItemAsync(_renterId, _listingId, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 5));

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.CannotRentOwnListing, result.Error);
        _cartRepoMock.Verify(x => x.CreateAsync(It.IsAny<RentalCart>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveCartItem_RepositoryUpdateFails_ReturnsCartNotFound()
    {
        var cart = RentalCart.Create(Guid.NewGuid(), _renterId, DateTimeOffset.UtcNow).Value;

        _cartRepoMock.Setup(x => x.GetActiveByRenterIdAsync(_renterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        // Setup UpdateAsync trả về false (lỗi version concurrency)
        _cartRepoMock.Setup(x => x.UpdateAsync(cart, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.RemoveCartItemAsync(_renterId, Guid.NewGuid()); // Guid bất kỳ vì Item chưa thêm

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.CartNotFound, result.Error);
    }
}