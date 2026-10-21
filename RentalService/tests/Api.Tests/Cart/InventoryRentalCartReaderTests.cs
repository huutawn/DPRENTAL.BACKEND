using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
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
        _inventory.Listings[_listingId] = TestListings.Create(
            _listingId, _ownerId, "Máy xúc Komatsu PC200", 3_000_000m, depositPerUnit: 20_000_000m);
        _reader = new InventoryRentalCartReader(_inventory);
    }

    [Fact]
    public async Task GetListing_AsksTheBatchApiForExactlyThatListing()
    {
        Result<RentalListingSnapshot> listing = await _reader.GetListingAsync(_listingId);

        Assert.True(listing.IsSuccess);
        Assert.Equal(_ownerId, listing.Value.OwnerId);
        Assert.Equal(1, _inventory.ListingReadCount);
        Assert.Equal([_listingId], _inventory.RequestedListingIds);
    }

    [Fact]
    public async Task GetListing_MapsPriceStatusAndDeposit()
    {
        RentalListingSnapshot listing = (await _reader.GetListingAsync(_listingId)).Value;

        Assert.Equal(_listingId, listing.ListingId);
        Assert.Equal(TestListings.EquipmentTypeId, listing.EquipmentTypeId);
        Assert.True(listing.IsPublished);
        Assert.True(listing.AcceptsBookings);
        Assert.Equal(3_000_000m, listing.DailyRate);
        Assert.Equal(20_000_000m, listing.Deposit);
        // Phiên bản giá gồm giá thuê và tiền cọc: một trong hai đổi thì phiên bản đổi.
        Assert.Equal("3000000:20000000", listing.PriceVersion);
    }

    [Fact]
    public async Task ListingInMissingIds_IsNotFound()
    {
        Result<RentalListingSnapshot> listing = await _reader.GetListingAsync(Guid.NewGuid());

        Assert.True(listing.IsFailure);
        Assert.Equal(ErrorType.NotFound, listing.Error.Type);
    }

    [Fact]
    public async Task InventoryDown_ReturnsServiceUnavailable()
    {
        _inventory.FailListingRead = true;

        Result<RentalListingSnapshot> listing = await _reader.GetListingAsync(_listingId);

        Assert.True(listing.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, listing.Error.Type);
    }
}
