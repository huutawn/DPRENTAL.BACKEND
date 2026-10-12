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
    /// <summary>Maximum rental duration in calendar days, including both endpoints.</summary>
    public const int MaximumRentalDays = 365;

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

    private RentalCart() { }

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
        if (id == Guid.Empty) return RentalErrors.CartNotFound;
        if (renterId == Guid.Empty) return RentalErrors.CartNotFound;
        return new RentalCart(id, renterId, utcNow);
    }

    public Result<RentalCartItem> AddItem(
        Guid itemId,
        Guid equipmentListingId,
        Guid equipmentListingOwnerId,
        int quantity,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate,
        DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;
        if (itemId == Guid.Empty || equipmentListingId == Guid.Empty) return RentalErrors.CartItemNotFound;

        Result requestValidation = ValidateRentalRequest(
            equipmentListingOwnerId, expectedStartDate, expectedEndDate);
        if (requestValidation.IsFailure) return requestValidation.Error;
        if (quantity <= 0) return RentalErrors.InvalidQuantity;

        RentalCartItem? existing = _items.Find(i =>
            i.EquipmentListingId == equipmentListingId &&
            i.ExpectedStartDate == expectedStartDate &&
            i.ExpectedEndDate == expectedEndDate);

        if (existing is not null)
        {
            if (quantity > int.MaxValue - existing.Quantity)
                return RentalErrors.QuantityOverflow;

            existing.AddQuantity(quantity, utcNow);
            UpdatedAt = utcNow;
            Version++;
            return existing;
        }

        RentalCartItem newItem = new(itemId, Id, equipmentListingId, quantity, expectedStartDate, expectedEndDate, utcNow);
        _items.Add(newItem);
        UpdatedAt = utcNow;
        Version++;
        return newItem;
    }

    public Result UpdateItemQuantity(
        Guid cartItemId,
        Guid equipmentListingOwnerId, // Check lại owner
        int newQuantity,
        DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;

        RentalCartItem? item = _items.Find(i => i.Id == cartItemId);
        if (item is null) return RentalErrors.CartItemNotFound;

        Result ownership = ValidateListingOwner(equipmentListingOwnerId);
        if (ownership.IsFailure) return ownership.Error;

        if (newQuantity < 1) return RentalErrors.InvalidQuantity;

        item.UpdateQuantity(newQuantity, utcNow);
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result UpdateItemDates(
        Guid cartItemId,
        Guid equipmentListingOwnerId,
        DateOnly newStartDate,
        DateOnly newEndDate,
        DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;

        RentalCartItem? item = _items.Find(i => i.Id == cartItemId);
        if (item is null) return RentalErrors.CartItemNotFound;

        Result requestValidation = ValidateRentalRequest(
            equipmentListingOwnerId, newStartDate, newEndDate);
        if (requestValidation.IsFailure) return requestValidation.Error;

        RentalCartItem? duplicate = _items.Find(i =>
            i.Id != cartItemId &&
            i.EquipmentListingId == item.EquipmentListingId &&
            i.ExpectedStartDate == newStartDate &&
            i.ExpectedEndDate == newEndDate);

        if (duplicate is not null)
        {
            if (item.Quantity > int.MaxValue - duplicate.Quantity)
                return RentalErrors.QuantityOverflow;

            duplicate.AddQuantity(item.Quantity, utcNow);
            _items.Remove(item);
        }
        else
        {
            item.UpdateDates(newStartDate, newEndDate, utcNow);
        }

        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    private Result ValidateRentalRequest(
        Guid equipmentListingOwnerId,
        DateOnly startDate,
        DateOnly endDate)
    {
        Result ownership = ValidateListingOwner(equipmentListingOwnerId);
        if (ownership.IsFailure) return ownership.Error;

        if (startDate > endDate) return RentalErrors.InvalidRentalPeriod;
        int durationInDays = endDate.DayNumber - startDate.DayNumber + 1;
        if (durationInDays > MaximumRentalDays)
        {
            return RentalErrors.DurationExceedsOneYear;
        }

        return Result.Success();
    }

    private Result ValidateListingOwner(Guid equipmentListingOwnerId)
    {
        if (equipmentListingOwnerId == Guid.Empty) return RentalErrors.OwnerIdRequired;
        if (equipmentListingOwnerId == RenterId) return RentalErrors.CannotRentOwnListing;
        return Result.Success();
    }

    // Các hàm RemoveItem, ClearItems, CompleteCheckout, Abandon giữ nguyên như cũ...
    public Result RemoveItem(Guid cartItemId, DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;
        RentalCartItem? item = _items.Find(i => i.Id == cartItemId);
        if (item is null) return RentalErrors.CartItemNotFound;
        _items.Remove(item);
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result ClearItems(DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;
        _items.Clear();
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result CompleteCheckout(IEnumerable<Guid> checkedOutCartItemIds, DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;
        ArgumentNullException.ThrowIfNull(checkedOutCartItemIds);
        HashSet<Guid> idSet = checkedOutCartItemIds.ToHashSet();
        if (idSet.Count == 0) return RentalErrors.CartEmpty;
        _items.RemoveAll(i => idSet.Contains(i.Id));
        if (_items.Count == 0) Status = RentalCartStatus.CheckedOut;
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }

    public Result Abandon(DateTimeOffset utcNow)
    {
        if (Status != RentalCartStatus.Active) return RentalErrors.CartNotActive;
        Status = RentalCartStatus.Abandoned;
        UpdatedAt = utcNow;
        Version++;
        return Result.Success();
    }
}
