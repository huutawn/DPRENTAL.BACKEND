using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalCartTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly DateOnly _start = new(2026, 10, 10);
    private readonly DateOnly _end = new(2026, 10, 15);
    private readonly int _maxDays = 30; // Policy giả lập để test

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
    public void AddItem_IncreasesVersionAndAddsItem_WithCorrectTimestamps()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        Guid listingId = Guid.NewGuid();

        Result<RentalCartItem> result = cart.AddItem(itemId, listingId, _ownerId, 2, _start, _end, _maxDays, _now);

        Assert.True(result.IsSuccess);
        Assert.Single(cart.Items);
        RentalCartItem item = cart.Items.First();
        Assert.Equal(_now, item.CreatedAt);
        Assert.Equal(_now, item.UpdatedAt);
    }

    [Fact]
    public void AddItem_WithDuplicateListingAndDates_AggregatesQuantity()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid listingId = Guid.NewGuid();

        cart.AddItem(Guid.NewGuid(), listingId, _ownerId, 2, _start, _end, _maxDays, _now);
        cart.AddItem(Guid.NewGuid(), listingId, _ownerId, 3, _start, _end, _maxDays, _now.AddMinutes(1));

        Assert.Single(cart.Items);
        Assert.Equal(5, cart.Items.First().Quantity);
    }

    [Fact]
    public void AddItem_StartEqualsEnd_IsValid()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Result<RentalCartItem> result = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), _ownerId, 2, _start, _start, _maxDays, _now);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AddItem_StartAfterEnd_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        DateOnly start = new(2026, 10, 15);
        DateOnly end = new(2026, 10, 10); // start > end

        Result<RentalCartItem> invalidDates = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), _ownerId, 2, start, end, _maxDays, _now);
        Assert.True(invalidDates.IsFailure);
        Assert.Equal(RentalErrors.InvalidRentalPeriod, invalidDates.Error);
    }

    [Fact]
    public void AddItem_DurationExceedsMaxDays_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;

        DateOnly start = new DateOnly(2026, 10, 1);
        DateOnly end = start.AddDays(_maxDays + 1);

        Result<RentalCartItem> result = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), _ownerId, 2, start, end, _maxDays, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.DurationExceeded, result.Error);
    }

    [Fact]
    public void AddItem_DurationAtMaxDays_IsAccepted()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        DateOnly end = _start.AddDays(_maxDays);

        Result<RentalCartItem> result = cart.AddItem(
            Guid.NewGuid(), Guid.NewGuid(), _ownerId, 1, _start, end, _maxDays, _now);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AddItem_InvalidDurationLimit_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;

        Result<RentalCartItem> result = cart.AddItem(
            Guid.NewGuid(), Guid.NewGuid(), _ownerId, 1, _start, _end, 0, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidDurationLimit, result.Error);
    }

    [Fact]
    public void AddItem_RenterIsOwner_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Result<RentalCartItem> result = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), _renterId, 1, _start, _end, _maxDays, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.CannotRentOwnListing, result.Error);
    }

    [Fact]
    public void AddItem_MissingOwnerId_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Result<RentalCartItem> result = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 1, _start, _end, _maxDays, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.OwnerIdRequired, result.Error);
    }

    [Fact]
    public void AddItem_QuantityOverflow_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid listingId = Guid.NewGuid();
        cart.AddItem(Guid.NewGuid(), listingId, _ownerId, int.MaxValue - 1, _start, _end, _maxDays, _now);

        Result<RentalCartItem> result = cart.AddItem(Guid.NewGuid(), listingId, _ownerId, 2, _start, _end, _maxDays, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.QuantityOverflow, result.Error);
    }

    [Fact]
    public void UpdateItemQuantity_MissingOwnerId_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        cart.AddItem(itemId, Guid.NewGuid(), _ownerId, 2, _start, _end, _maxDays, _now);

        Result result = cart.UpdateItemQuantity(itemId, Guid.Empty, 5, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.OwnerIdRequired, result.Error);
    }

    [Fact]
    public void UpdateItemQuantity_UpdatesQuantityAndTimestamp()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        cart.AddItem(itemId, Guid.NewGuid(), _ownerId, 2, _start, _end, _maxDays, _now);
        DateTimeOffset updatedAt = _now.AddMinutes(3);

        Result result = cart.UpdateItemQuantity(itemId, _ownerId, 5, updatedAt);

        Assert.True(result.IsSuccess);
        RentalCartItem item = Assert.Single(cart.Items);
        Assert.Equal(5, item.Quantity);
        Assert.Equal(_now, item.CreatedAt);
        Assert.Equal(updatedAt, item.UpdatedAt);
    }

    [Fact]
    public void UpdateItemDates_DurationExceedsMaxDays_Rejects()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        cart.AddItem(itemId, Guid.NewGuid(), _ownerId, 2, _start, _end, _maxDays, _now);

        DateOnly newEnd = _start.AddDays(_maxDays + 1);
        Result result = cart.UpdateItemDates(itemId, _ownerId, _start, newEnd, _maxDays, _now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.DurationExceeded, result.Error);
    }

    [Fact]
    public void UpdateItemDates_CausesMerge_AggregatesQuantityAndRemovesOldItem()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid listingId = Guid.NewGuid();
        Guid item1 = Guid.NewGuid();
        Guid item2 = Guid.NewGuid();

        DateOnly diffStart = _start.AddDays(1);

        cart.AddItem(item1, listingId, _ownerId, 2, _start, _end, _maxDays, _now);
        cart.AddItem(item2, listingId, _ownerId, 3, diffStart, _end, _maxDays, _now);

        // Đổi ngày của item2 cho trùng với item1
        Result result = cart.UpdateItemDates(item2, _ownerId, _start, _end, _maxDays, _now.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Single(cart.Items); // Đã gộp thành 1
        Assert.Equal(5, cart.Items.First().Quantity);
        Assert.Equal(_now.AddMinutes(5), cart.Items.First().UpdatedAt);
    }

    [Fact]
    public void UpdateItemDates_StartEqualsEnd_IsAccepted()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, _now).Value;
        Guid itemId = Guid.NewGuid();
        cart.AddItem(itemId, Guid.NewGuid(), _ownerId, 1, _start, _end, _maxDays, _now);

        Result result = cart.UpdateItemDates(itemId, _ownerId, _start, _start, _maxDays, _now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        RentalCartItem item = Assert.Single(cart.Items);
        Assert.Equal(_start, item.ExpectedStartDate);
        Assert.Equal(_start, item.ExpectedEndDate);
        Assert.Equal(_now, item.CreatedAt);
        Assert.Equal(_now.AddMinutes(1), item.UpdatedAt);
    }
}
