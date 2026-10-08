using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalCartTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void CreateCart_InitializesWithActiveStatusAndVersionOne()
    {
        Guid cartId = Guid.NewGuid();
        Result<RentalCart> result = RentalCart.Create(cartId, _renterId, _now);

        Assert.True(result.IsSuccess);
        RentalCart cart = result.Value;
        Assert.Equal(cartId, cart.Id);
        Assert.Equal(_renterId, cart.RenterId);
        Assert.Equal(RentalCartStatus.Active, cart.Status);
        Assert.Equal(1, cart.Version);
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void CreateCart_FailsWithEmptyIds()
    {
        Result<RentalCart> emptyId = RentalCart.Create(Guid.Empty, _renterId, _now);
        Assert.True(emptyId.IsFailure);

        Result<RentalCart> emptyRenter = RentalCart.Create(Guid.NewGuid(), Guid.Empty, _now);
        Assert.True(emptyRenter.IsFailure);
    }

    [Fact]
    public void AddItem_IncreasesVersionAndAddsItem()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        Guid listingId = Guid.NewGuid();
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 15);

        Result<RentalCartItem> result = cart.AddItem(itemId, listingId, 2, start, end, _now);

        Assert.True(result.IsSuccess);
        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Version);

        RentalCartItem item = cart.Items.First();
        Assert.Equal(itemId, item.Id);
        Assert.Equal(listingId, item.EquipmentListingId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(start, item.ExpectedStartDate);
        Assert.Equal(end, item.ExpectedEndDate);
    }

    [Fact]
    public void AddItem_WithDuplicateListingAndDates_AggregatesQuantity()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid listingId = Guid.NewGuid();
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 15);

        cart.AddItem(Guid.NewGuid(), listingId, 2, start, end, _now);
        cart.AddItem(Guid.NewGuid(), listingId, 3, start, end, _now.AddMinutes(1));

        Assert.Single(cart.Items);
        Assert.Equal(5, cart.Items.First().Quantity);
        Assert.Equal(3, cart.Version);
    }

    [Fact]
    public void AddItem_RejectsInvalidQuantityAndDates()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid listingId = Guid.NewGuid();
        DateOnly start = new(2026, 10, 15);
        DateOnly end = new(2026, 10, 10);

        Result<RentalCartItem> zeroQty = cart.AddItem(Guid.NewGuid(), listingId, 0, start, end, _now);
        Assert.True(zeroQty.IsFailure);

        Result<RentalCartItem> invalidDates = cart.AddItem(Guid.NewGuid(), listingId, 2, start, end, _now);
        Assert.True(invalidDates.IsFailure);
    }

    [Fact]
    public void UpdateItem_ModifiesItemAndIncrementsVersion()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        cart.AddItem(itemId, Guid.NewGuid(), 2, new(2026, 10, 10), new(2026, 10, 12), _now);

        Result result = cart.UpdateItem(itemId, 5, new(2026, 10, 11), new(2026, 10, 14), _now.AddMinutes(2));

        Assert.True(result.IsSuccess);
        RentalCartItem updated = cart.Items.First();
        Assert.Equal(5, updated.Quantity);
        Assert.Equal(new DateOnly(2026, 10, 11), updated.ExpectedStartDate);
        Assert.Equal(new DateOnly(2026, 10, 14), updated.ExpectedEndDate);
    }

    [Fact]
    public void RemoveAndClearItems_WorkCorrectly()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid item1 = Guid.NewGuid();
        Guid item2 = Guid.NewGuid();
        cart.AddItem(item1, Guid.NewGuid(), 1, new(2026, 10, 10), new(2026, 10, 12), _now);
        cart.AddItem(item2, Guid.NewGuid(), 2, new(2026, 10, 13), new(2026, 10, 15), _now);

        Result removeRes = cart.RemoveItem(item1, _now);
        Assert.True(removeRes.IsSuccess);
        Assert.Single(cart.Items);

        Result clearRes = cart.ClearItems(_now);
        Assert.True(clearRes.IsSuccess);
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void CompleteCheckout_RemovesItems_AndTransitionsStatus()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid item1 = Guid.NewGuid();
        Guid item2 = Guid.NewGuid();
        cart.AddItem(item1, Guid.NewGuid(), 1, new(2026, 10, 10), new(2026, 10, 12), _now);
        cart.AddItem(item2, Guid.NewGuid(), 2, new(2026, 10, 13), new(2026, 10, 15), _now);

        // Checkout only item1: cart should remain Active because item2 is left
        Result partialCheckout = cart.CompleteCheckout([item1], _now);
        Assert.True(partialCheckout.IsSuccess);
        Assert.Equal(RentalCartStatus.Active, cart.Status);
        Assert.Single(cart.Items);
        Assert.Equal(item2, cart.Items.First().Id);

        // Checkout remaining item2: cart should become CheckedOut
        Result completeCheckout = cart.CompleteCheckout([item2], _now);
        Assert.True(completeCheckout.IsSuccess);
        Assert.Equal(RentalCartStatus.CheckedOut, cart.Status);
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void Abandon_TransitionsStatusToAbandoned()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Result result = cart.Abandon(_now);

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalCartStatus.Abandoned, cart.Status);

        // Cannot add items to abandoned cart
        Result addRes = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), 1, new(2026, 10, 10), new(2026, 10, 12), _now);
        Assert.True(addRes.IsFailure);
    }
}
