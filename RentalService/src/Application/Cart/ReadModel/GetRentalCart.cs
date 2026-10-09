using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Cart.ReadModel;

public sealed class GetRentalCart(
    IRentalCartRepository carts,
    IRentalListingReader listingReader,
    IRentalAvailabilityReader availabilityReader,
    IRentalCartPolicy policy)
{
    public async Task<Result<RentalCartReadModel>> ExecuteAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        if (renterId == Guid.Empty) return Result.Success(new RentalCartReadModel(Guid.Empty, []));

        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        if (cart is null || cart.Status != RentalCartStatus.Active)
            return Result.Success(new RentalCartReadModel(cart?.Id ?? Guid.Empty, []));

        var lines = new List<RentalCartLineReadModel>();

        foreach (RentalCartItem item in cart.Items)
        {
            int duration = item.ExpectedEndDate.DayNumber - item.ExpectedStartDate.DayNumber;

            // Đồng bộ: Đã chặn triệt để Start == End (duration <= 0)
            if (item.Quantity < 1 || duration <= 0 || duration > policy.MaximumRentalDays)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.ValidationFailed, "Kỳ thuê hoặc số lượng không hợp lệ."));
                continue;
            }

            RentalListingSnapshot? listing = await listingReader.GetListingAsync(item.EquipmentListingId, cancellationToken);
            if (listing is null)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.Unavailable, "Sản phẩm không tồn tại hoặc đã bị xóa."));
                continue;
            }

            // MỚI: Check listing do chính mình sở hữu
            if (listing.OwnerId == renterId)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.ValidationFailed, "Không thể thuê thiết bị do chính mình sở hữu."));
                continue;
            }

            if (!listing.IsPublished || !listing.AcceptsBookings)
            {
                lines.Add(CreateLine(item, listing, RentalCartLineStatus.ListingDisabled, "Sản phẩm hiện không nhận đơn thuê."));
                continue;
            }

            int? availableQty = await availabilityReader.GetAvailableQuantityAsync(
                item.EquipmentListingId, item.ExpectedStartDate, item.ExpectedEndDate, cancellationToken);

            // Phân biệt Null (Lỗi hệ thống) và 0 (hết hàng)
            if (availableQty is null)
            {
                lines.Add(CreateLine(item, listing, RentalCartLineStatus.ValidationFailed, "Không thể kiểm tra tồn kho tại thời điểm này."));
                continue;
            }

            if (availableQty < item.Quantity)
            {
                lines.Add(CreateLine(item, listing, RentalCartLineStatus.InsufficientQuantity, $"Chỉ còn {availableQty} sản phẩm khả dụng."));
                continue;
            }

            lines.Add(CreateLine(item, listing, RentalCartLineStatus.Available, null));
        }

        return Result.Success(new RentalCartReadModel(cart.Id, lines));
    }

    private static RentalCartLineReadModel CreateFailedLine(RentalCartItem item, RentalCartLineStatus status, string message)
    {
        return new RentalCartLineReadModel(
            item.Id, item.EquipmentListingId, item.Quantity, item.ExpectedStartDate, item.ExpectedEndDate,
            CurrentDailyRate: null, CurrentDeposit: null, EstimatedRentalAmount: null, status, message);
    }

    private static RentalCartLineReadModel CreateLine(RentalCartItem item, RentalListingSnapshot listing, RentalCartLineStatus status, string? message)
    {
        int duration = item.ExpectedEndDate.DayNumber - item.ExpectedStartDate.DayNumber;

        // MỚI: Đã bổ sung nhân thêm item.Quantity
        decimal estimatedAmount = duration * listing.DailyRate * item.Quantity;

        return new RentalCartLineReadModel(
            item.Id, item.EquipmentListingId, item.Quantity, item.ExpectedStartDate, item.ExpectedEndDate,
            listing.DailyRate, listing.Deposit, estimatedAmount, status, message);
    }
}