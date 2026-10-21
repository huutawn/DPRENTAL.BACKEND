using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Services.Cart;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cart;

/// <summary>
/// Giỏ hàng đọc thông tin máy qua API batch của Inventory (POST /equipment-listings/batch) với một mã mỗi lần.
/// </summary>
public sealed class InventoryRentalCartReaderTests
{
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeInventoryListingClient _inventory = new();
    private readonly InventoryRentalCartReader _reader;

    public InventoryRentalCartReaderTests()
    {
        _inventory.Listings[_listingId] = TestListings.Create(_listingId, _ownerId, "Máy xúc Komatsu PC200", 3_000_000m);
        _reader = new InventoryRentalCartReader(_inventory);
    }

    [Fact]
    public async Task GetOwnerId_AsksTheBatchApiForExactlyThatListing()
    {
        Guid? ownerId = await _reader.GetOwnerIdAsync(_listingId);

        Assert.Equal(_ownerId, ownerId);
        Assert.Equal(1, _inventory.ListingReadCount);
        Assert.Equal([_listingId], _inventory.RequestedListingIds);
    }

    [Fact]
    public async Task GetListing_MapsPriceStatusAndZeroDeposit()
    {
        RentalListingSnapshot? listing = await _reader.GetListingAsync(_listingId);

        Assert.NotNull(listing);
        Assert.Equal(_listingId, listing.ListingId);
        Assert.Equal(_ownerId, listing.OwnerId);
        Assert.True(listing.IsPublished);
        Assert.True(listing.AcceptsBookings);
        Assert.Equal(3_000_000m, listing.DailyRate);
        // Inventory chưa có tiền cọc; phiên bản giá là chính giá thuê.
        Assert.Equal(0m, listing.Deposit);
        Assert.Equal("3000000", listing.PriceVersion);
    }

    [Fact]
    public async Task ListingInMissingIds_IsTreatedAsNotFound()
    {
        Guid unknown = Guid.NewGuid();

        Assert.Null(await _reader.GetOwnerIdAsync(unknown));
        Assert.Null(await _reader.GetListingAsync(unknown));
    }

    [Fact]
    public async Task EmptyListingId_IsNotFound_WithoutCallingInventory()
    {
        Assert.Null(await _reader.GetOwnerIdAsync(Guid.Empty));
        Assert.Null(await _reader.GetListingAsync(Guid.Empty));
        Assert.Equal(0, _inventory.ListingReadCount);
    }

    [Fact]
    public async Task InventoryDown_ThrowsDependencyFailure()
    {
        _inventory.FailListingRead = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _reader.GetListingAsync(_listingId));
    }
}
