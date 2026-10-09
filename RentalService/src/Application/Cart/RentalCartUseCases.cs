using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Cart;

public sealed class RentalCartUseCases(
    IRentalCartRepository carts,
    IRentalListingOwnerProvider listingOwners,
    TimeProvider timeProvider)
{
    public async Task<Result<RentalCartItem>> AddCartItemAsync(
        Guid renterId,
        Guid equipmentListingId,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (renterId == Guid.Empty)
            return RentalErrors.CartNotFound;

        Guid? ownerId = await listingOwners.GetOwnerIdAsync(
            equipmentListingId,
            cancellationToken);

        if (ownerId is null)
            return RentalErrors.OwnerIdRequired;

        RentalCart? cart = await carts.GetByRenterIdAsync(renterId, cancellationToken);
        bool isNewCart = false;

        // Check trực tiếp ở đây để C# Analyzer hiểu được null-state
        if (cart is null || cart.Status != RentalCartStatus.Active)
        {
            Result<RentalCart> newCart = RentalCart.Create(
                Guid.NewGuid(),
                renterId,
                timeProvider.GetUtcNow());

            if (newCart.IsFailure)
                return newCart.Error;

            cart = newCart.Value;
            isNewCart = true;
        }

        // Lúc này C# Compiler đã chắc chắn 100% cart không thể null
        Result<RentalCartItem> added = cart.AddItem(
            Guid.NewGuid(),
            equipmentListingId,
            ownerId.Value,
            quantity,
            startDate,
            endDate,
            timeProvider.GetUtcNow());

        if (added.IsFailure)
            return added.Error;

        if (isNewCart)
        {
            await carts.CreateAsync(cart, cancellationToken);
        }
        else if (!await carts.UpdateAsync(cart, cancellationToken))
        {
            return RentalErrors.CartNotFound;
        }

        return added.Value;
    }

    public async Task<Result> UpdateCartItemQuantityAsync(
        Guid renterId,
        Guid cartItemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
            return RentalErrors.CartItemNotFound;

        Guid? ownerId = await listingOwners.GetOwnerIdAsync(
            item.EquipmentListingId,
            cancellationToken);

        if (ownerId is null)
            return RentalErrors.OwnerIdRequired;

        Result updated = cart.UpdateItemQuantity(
            cartItemId,
            ownerId.Value, // Pass retrieved ownerId to domain
            quantity,
            timeProvider.GetUtcNow());

        if (updated.IsFailure)
            return updated.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : RentalErrors.CartNotFound;
    }

    public async Task<Result> UpdateCartItemDatesAsync(
        Guid renterId,
        Guid cartItemId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
            return RentalErrors.CartItemNotFound;

        Guid? ownerId = await listingOwners.GetOwnerIdAsync(
            item.EquipmentListingId,
            cancellationToken);

        if (ownerId is null)
            return RentalErrors.OwnerIdRequired;

        Result updated = cart.UpdateItemDates(
            cartItemId,
            ownerId.Value,
            startDate,
            endDate,
            timeProvider.GetUtcNow());

        if (updated.IsFailure)
            return updated.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : RentalErrors.CartNotFound;
    }

    public async Task<Result> RemoveCartItemAsync(
        Guid renterId,
        Guid cartItemId,
        CancellationToken cancellationToken = default)
    {
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        Result removed = cart.RemoveItem(cartItemId, timeProvider.GetUtcNow());
        if (removed.IsFailure)
            return removed.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : RentalErrors.CartNotFound;
    }

    public async Task<Result> ClearCartAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        Result cleared = cart.ClearItems(timeProvider.GetUtcNow());
        if (cleared.IsFailure)
            return cleared.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : RentalErrors.CartNotFound;
    }

    private async Task<RentalCart?> GetActiveCartAsync(
        Guid renterId,
        CancellationToken cancellationToken)
    {
        if (renterId == Guid.Empty)
            return null;

        RentalCart? cart = await carts.GetByRenterIdAsync(renterId, cancellationToken);
        return cart?.Status == RentalCartStatus.Active ? cart : null;
    }
}