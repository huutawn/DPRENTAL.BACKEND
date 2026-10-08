using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_carts", Schema = "rental")]
[Index(nameof(RenterId))]
public class RentalCart
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RenterId { get; private set; }

    public RentalCartStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    [ConcurrencyCheck]
    public long Version { get; private set; }

    private readonly List<RentalCartItem> _items = [];
    public IReadOnlyCollection<RentalCartItem> Items => _items.AsReadOnly();

    // Constructor for EF Core
    private RentalCart()
    {
    }

    private RentalCart(Guid id, Guid renterId, DateTimeOffset utcNow)
    {
        Id = id;
        RenterId = renterId;
        Status = RentalCartStatus.Active;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
        Version = 1;
    }

    public static Result<RentalCart> Create(Guid id, Guid renterId, DateTimeOffset utcNow)
    {
        if (id == Guid.Empty)
        {
            return RentalErrors.CartNotFound;
        }

        if (renterId == Guid.Empty)
        {
            return RentalErrors.CartNotFound;
        }

        return new RentalCart(id, renterId, utcNow);
    }

    public Result<RentalCartItem> AddItem(
        Guid itemId,
        Guid equipmentListingId,
        int quantity,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate,
        DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active)
        {
            return RentalErrors.CartNotActive;
        }

        if (itemId == Guid.Empty || equipmentListingId == Guid.Empty)
        {
            return RentalErrors.CartItemNotFound;
        }

        if (quantity <= 0)
        {
            return RentalErrors.InvalidQuantity;
        }

        if (expectedStartDate >= expectedEndDate)
        {
            return RentalErrors.InvalidRentalPeriod;
        }

        RentalCartItem? existing = _items.Find(i =>
            i.EquipmentListingId == equipmentListingId &&
            i.ExpectedStartDate == expectedStartDate &&
            i.ExpectedEndDate == expectedEndDate);

        if (existing is not null)
        {
            existing.AddQuantity(quantity);
            UpdatedAt = utcNow;
            Version++;
            return existing;
        }

        RentalCartItem newItem = new(itemId, Id, equipmentListingId, quantity, expectedStartDate, expectedEndDate);
        _items.Add(newItem);
        UpdatedAt = utcNow;
        Version++;
        return newItem;
    }

    public Result UpdateItem(
        Guid cartItemId,
        int newQuantity,
        DateOnly newStartDate,
        DateOnly newEndDate,
        DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active)
        {
            return RentalErrors.CartNotActive;
        }

        if (newQuantity <= 0)
        {
            return RentalErrors.InvalidQuantity;
        }

        if (newStartDate >= newEndDate)
        {
            return RentalErrors.InvalidRentalPeriod;
        }

        RentalCartItem? item = _items.Find(i => i.Id == cartItemId);
        if (item is null)
        {
            return RentalErrors.CartItemNotFound;
        }

        item.Update(newQuantity, newStartDate, newEndDate);
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result RemoveItem(Guid cartItemId, DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active)
        {
            return RentalErrors.CartNotActive;
        }

        RentalCartItem? item = _items.Find(i => i.Id == cartItemId);
        if (item is null)
        {
            return RentalErrors.CartItemNotFound;
        }

        _items.Remove(item);
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result ClearItems(DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active)
        {
            return RentalErrors.CartNotActive;
        }

        _items.Clear();
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result CompleteCheckout(IEnumerable<Guid> checkedOutCartItemIds, DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active)
        {
            return RentalErrors.CartNotActive;
        }

        ArgumentNullException.ThrowIfNull(checkedOutCartItemIds);
        HashSet<Guid> idSet = checkedOutCartItemIds.ToHashSet();

        if (idSet.Count == 0)
        {
            return RentalErrors.CartEmpty;
        }

        _items.RemoveAll(i => idSet.Contains(i.Id));

        if (_items.Count == 0)
        {
            Status = RentalCartStatus.CheckedOut;
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result Abandon(DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active)
        {
            return RentalErrors.CartNotActive;
        }

        Status = RentalCartStatus.Abandoned;
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }
}
