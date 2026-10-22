using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Cart;

public sealed class RentalCartUseCases(
    IRentalCartRepository carts,
    RentalCartItemEvaluator evaluator,
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
        if (renterId == Guid.Empty)
            return RentalErrors.CartNotFound;

        if (equipmentListingId == Guid.Empty)
            return RentalErrors.CartItemNotFound;

        if (quantity <= 0)
            return RentalErrors.InvalidQuantity;

        Result dateCheck = evaluator.ValidateRentalDates(startDate, endDate);
        if (dateCheck.IsFailure)
            return dateCheck.Error;

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

        RentalCartItem? existing = cart.Items.FirstOrDefault(i =>
            i.EquipmentListingId == equipmentListingId &&
            i.ExpectedStartDate == startDate &&
            i.ExpectedEndDate == endDate);

        int targetQuantity = quantity;
        if (existing is not null)
        {
            if (quantity > int.MaxValue - existing.Quantity)
                return RentalErrors.QuantityOverflow;

            targetQuantity = existing.Quantity + quantity;
        }

        Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
            renterId,
            equipmentListingId,
            targetQuantity,
            startDate,
            endDate,
            cancellationToken);

        if (evalResult.IsFailure)
        {
            return evalResult.Error;
        }

        RentalCartItemEvaluationResult evaluation = evalResult.Value;
        Result checkResult = MapEvaluationToMutationResult(evaluation, allowDatePassed: false);
        if (checkResult.IsFailure)
        {
            return checkResult.Error;
        }

        RentalListingSnapshot? listing = evaluation.Snapshot;
        if (listing is null)
        {
            return RentalErrors.ListingUnavailable;
        }

        if (listing.EquipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "EquipmentTypeId của listing không được để trống hoặc rỗng.");
        }

        bool isNewItem = existing is null;

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
        if (quantity < 1)
            return RentalErrors.InvalidQuantity;

        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
            return RentalErrors.CartItemNotFound;

        // Đánh giá dữ liệu đích bằng evaluator duy nhất (không tự gọi availability hay check riêng)
        Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
            renterId,
            item.EquipmentListingId,
            quantity,
            item.ExpectedStartDate,
            item.ExpectedEndDate,
            cancellationToken);

        if (evalResult.IsFailure)
        {
            return evalResult.Error;
        }

        RentalCartItemEvaluationResult evaluation = evalResult.Value;
        Result checkResult = MapEvaluationToMutationResult(evaluation, allowDatePassed: true);
        if (checkResult.IsFailure)
        {
            return checkResult.Error;
        }

        Guid ownerId = evaluation.Snapshot?.OwnerId ?? Guid.Empty;
        if (ownerId == Guid.Empty)
        {
            return RentalErrors.ListingUnavailable;
        }

        Result updated = cart.UpdateItemQuantity(
            cartItemId,
            ownerId,
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
        Result dateCheck = evaluator.ValidateRentalDates(startDate, endDate);
        if (dateCheck.IsFailure)
            return dateCheck.Error;

        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        if (cart is null)
            return RentalErrors.CartNotFound;

        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
            return RentalErrors.CartItemNotFound;

        RentalCartItem? duplicate = cart.Items.FirstOrDefault(i =>
            i.Id != cartItemId &&
            i.EquipmentListingId == item.EquipmentListingId &&
            i.ExpectedStartDate == startDate &&
            i.ExpectedEndDate == endDate);

        int targetQuantity = item.Quantity;
        if (duplicate is not null)
        {
            if (item.Quantity > int.MaxValue - duplicate.Quantity)
                return RentalErrors.QuantityOverflow;

            targetQuantity = duplicate.Quantity + item.Quantity;
        }

        Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
            renterId,
            item.EquipmentListingId,
            targetQuantity,
            startDate,
            endDate,
            cancellationToken);

        if (evalResult.IsFailure)
        {
            return evalResult.Error;
        }

        RentalCartItemEvaluationResult evaluation = evalResult.Value;
        Result checkResult = MapEvaluationToMutationResult(evaluation, allowDatePassed: false);
        if (checkResult.IsFailure)
        {
            return checkResult.Error;
        }

        Guid ownerId = evaluation.Snapshot?.OwnerId ?? Guid.Empty;
        if (ownerId == Guid.Empty)
        {
            return RentalErrors.ListingUnavailable;
        }

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

    private static Result MapEvaluationToMutationResult(
        RentalCartItemEvaluationResult evaluation,
        bool allowDatePassed)
    {
        if (evaluation.Status == RentalCartLineStatus.Available)
        {
            return Result.Success();
        }

        if (evaluation.Status == RentalCartLineStatus.DatePassed && allowDatePassed)
        {
            return Result.Success();
        }

        if (evaluation.Status == RentalCartLineStatus.Unavailable ||
            evaluation.Status == RentalCartLineStatus.ListingDisabled)
        {
            return RentalErrors.ListingUnavailable;
        }

        if (evaluation.InteractionStatus == RentalCartItemInteractionStatus.DisabledReadOnly &&
            evaluation.Status == RentalCartLineStatus.ValidationFailed)
        {
            return RentalErrors.CannotRentOwnListing;
        }

        if (evaluation.Status == RentalCartLineStatus.InsufficientQuantity)
        {
            return RentalErrors.InsufficientAvailability(evaluation.AvailableQuantity ?? 0);
        }

        string errorCode = !string.IsNullOrEmpty(evaluation.ErrorCode)
            ? evaluation.ErrorCode
            : (evaluation.Status == RentalCartLineStatus.DatePassed ? "Cart.StartDateInvalid" : "Cart.ValidationFailed");

        return Error.Validation(errorCode, evaluation.Message ?? "Mục thuê không hợp lệ.");
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
