using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;

namespace Dprental.RentalService.Application.Services.Cart;

public sealed class RentalCartUseCases(
    IRentalCartRepository carts,
    IRentalListingReader listingReader,
    IOrderLockManager lockManager,
    IUnitOfWork unitOfWork,
    IRentalEventProducer eventProducer,
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
        Result dateCheck = ValidateRentalDates(startDate, endDate);
        if (dateCheck.IsFailure) return dateCheck.Error;
        if (renterId == Guid.Empty)
            return RentalErrors.CartNotFound;

        Result<RentalListingSnapshot> listingResult = await listingReader.GetListingAsync(
            equipmentListingId,
            cancellationToken);

        if (listingResult.IsFailure)
        {
            if (listingResult.Error.Type == ErrorType.NotFound)
                return RentalErrors.OwnerIdRequired;
            return listingResult.Error;
        }

        RentalListingSnapshot listing = listingResult.Value;
        if (listing.EquipmentTypeId == Guid.Empty)
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "EquipmentTypeId của listing không được để trống hoặc rỗng.");

        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        bool isNewCart = false;

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

        bool isNewItem = !cart.Items.Any(i =>
            i.EquipmentListingId == equipmentListingId &&
            i.ExpectedStartDate == startDate &&
            i.ExpectedEndDate == endDate);

        Result<RentalCartItem> added = cart.AddItem(
            Guid.NewGuid(),
            equipmentListingId,
            listing.OwnerId,
            listing.EquipmentTypeId,
            quantity,
            startDate,
            endDate,
            timeProvider.GetUtcNow());

        if (added.IsFailure)
            return added.Error;

        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            if (isNewCart)
            {
                await carts.CreateAsync(cart, cancellationToken);
            }
            else
            {
                bool updated = await carts.UpdateAsync(cart, cancellationToken);
                if (!updated)
                {
                    await tx.RollbackAsync(cancellationToken);
                    unitOfWork.DiscardChanges();
                    return Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
                }
            }

            if (isNewItem)
            {
                var enqueueResult = await eventProducer.EnqueueCartItemAddedAsync(
                    new CartItemAddedPayload(
                        renterId,
                        equipmentListingId,
                        listing.EquipmentTypeId,
                        cart.Id,
                        added.Value.Id),
                    cancellationToken: cancellationToken);

                if (enqueueResult.IsFailure)
                {
                    await tx.RollbackAsync(cancellationToken);
                    unitOfWork.DiscardChanges();
                    return enqueueResult.Error;
                }
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return added.Value;
        }
        catch (Exception)
        {
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }
    }
    public async Task<Result> UpdateCartItemAsync(
        Guid renterId,
        Guid cartItemId,
        UpdateCartItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool hasAnyDate = request.ExpectedStartDate is not null || request.ExpectedEndDate is not null;
        bool hasBothDates = request.ExpectedStartDate is not null && request.ExpectedEndDate is not null;
        bool hasQuantity = request.Quantity is not null;

        if ((hasAnyDate && !hasBothDates) || hasQuantity == hasBothDates)
        {
            return RentalErrors.InvalidCartUpdate;
        }

        if (hasQuantity)
        {
            return await UpdateCartItemQuantityAsync(
                renterId, cartItemId, request.Quantity.GetValueOrDefault(), cancellationToken);
        }

        return await UpdateCartItemDatesAsync(
            renterId, cartItemId, request.ExpectedStartDate.GetValueOrDefault(), request.ExpectedEndDate.GetValueOrDefault(), cancellationToken);
    }


    public async Task<Result> UpdateCartItemQuantityAsync(
        Guid renterId,
        Guid cartItemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
            return RentalErrors.CartItemNotFound;

        Result<RentalListingSnapshot> listingResult = await listingReader.GetListingAsync(
            item.EquipmentListingId,
            cancellationToken);

        if (listingResult.IsFailure)
        {
            if (listingResult.Error.Type == ErrorType.NotFound)
                return RentalErrors.OwnerIdRequired;
            return listingResult.Error;
        }

        RentalListingSnapshot listing = listingResult.Value;
        Guid ownerId = listing.OwnerId;

        Result updated = cart.UpdateItemQuantity(
            cartItemId,
            ownerId, // Pass retrieved ownerId to domain
            quantity,
            timeProvider.GetUtcNow());

        if (updated.IsFailure)
            return updated.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    public async Task<Result> UpdateCartItemDatesAsync(
        Guid renterId,
        Guid cartItemId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        Result dateCheck = ValidateRentalDates(startDate, endDate); if (dateCheck.IsFailure) return dateCheck.Error;
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
            return RentalErrors.CartItemNotFound;

        Result<RentalListingSnapshot> listingResult = await listingReader.GetListingAsync(
            item.EquipmentListingId,
            cancellationToken);

        if (listingResult.IsFailure)
        {
            if (listingResult.Error.Type == ErrorType.NotFound)
                return RentalErrors.OwnerIdRequired;
            return listingResult.Error;
        }

        RentalListingSnapshot listing = listingResult.Value;
        Guid ownerId = listing.OwnerId;

        Result updated = cart.UpdateItemDates(
            cartItemId,
            ownerId,
            startDate,
            endDate,
            timeProvider.GetUtcNow());

        if (updated.IsFailure)
            return updated.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    public async Task<Result> RemoveCartItemAsync(
        Guid renterId,
        Guid cartItemId,
        CancellationToken cancellationToken = default)
    {
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        Result removed = cart.RemoveItem(cartItemId, timeProvider.GetUtcNow());
        if (removed.IsFailure)
            return removed.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    public async Task<Result> ClearCartAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        Result cleared = cart.ClearItems(timeProvider.GetUtcNow());
        if (cleared.IsFailure)
            return cleared.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }
    private Result ValidateRentalDates(DateOnly startDate, DateOnly endDate)
    {
        DateOnly todayVn = DateOnly.FromDateTime(timeProvider.GetUtcNow().AddHours(7).DateTime);

        if (startDate > endDate) return RentalErrors.InvalidRentalPeriod;

        int durationDays = endDate.DayNumber - startDate.DayNumber + 1;
        if (durationDays > 365) return RentalErrors.DurationExceedsOneYear; // Đảm bảo khai báo Error này trong RentalErrors

        if (RentalBookingPolicy.IsStartDateTooSoon(startDate, todayVn))
            return Error.Validation("Cart.StartDateInvalid", RentalBookingPolicy.TooSoonMessage(todayVn));

        if (startDate > todayVn.AddMonths(6))
            return Error.Validation("Cart.AdvanceBookingTooFar", "Chỉ được phép đặt trước tối đa 6 tháng.");

        return Result.Success();
    }

    private async Task<RentalCart?> GetActiveCartAsync(
        Guid renterId,
        CancellationToken cancellationToken)
    {
        if (renterId == Guid.Empty)
            return null;

        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        return cart?.Status == RentalCartStatus.Active ? cart : null;
    }
}
