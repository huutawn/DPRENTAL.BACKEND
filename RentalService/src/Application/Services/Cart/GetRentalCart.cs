using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;

namespace Dprental.RentalService.Application.Services.Cart;

public sealed class GetRentalCart(
    IRentalCartRepository carts,
    IRentalListingReader listingReader,
    IRentalAvailabilityReader availabilityReader,
    IRentalCartPolicy policy,
    TimeProvider timeProvider)
{
    public async Task<Result<RentalCartReadModel>> ExecuteAsync(
        Guid renterId,
        IReadOnlyDictionary<Guid, string>? clientPriceVersions,
        CancellationToken cancellationToken = default)
    {
        if (renterId == Guid.Empty) return Result.Success(new RentalCartReadModel(Guid.Empty, []));

        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        if (cart is null || cart.Status != RentalCartStatus.Active)
            return Result.Success(new RentalCartReadModel(cart?.Id ?? Guid.Empty, []));

        var lines = new List<RentalCartLineReadModel>();

        DateOnly todayVn = DateOnly.FromDateTime(timeProvider.GetUtcNow().AddHours(7).DateTime);
        DateOnly maxAdvanceDate = todayVn.AddMonths(6);

        foreach (RentalCartItem item in cart.Items)
        {
            int duration = item.ExpectedEndDate.DayNumber - item.ExpectedStartDate.DayNumber + 1;

            if (item.ExpectedStartDate > item.ExpectedEndDate || item.Quantity < 1)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.ValidationFailed, "Kỳ thuê hoặc số lượng không hợp lệ."));
                continue;
            }

            if (duration > policy.MaximumRentalDays)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.ValidationFailed, $"Thời gian thuê không được vượt quá {policy.MaximumRentalDays} ngày."));
                continue;
            }

            // MỚI: Kiểm tra ngày thuê (DatePassed và 6 tháng)
            if (RentalBookingPolicy.IsStartDateTooSoon(item.ExpectedStartDate, todayVn))
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.DatePassed, RentalBookingPolicy.TooSoonMessage(todayVn)));
                continue;
            }
            if (item.ExpectedStartDate > maxAdvanceDate)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.ValidationFailed, "Chỉ được phép đặt thuê trước tối đa 6 tháng."));
                continue;
            }

            Result<RentalListingSnapshot> listingResult = await listingReader.GetListingAsync(item.EquipmentListingId, cancellationToken);
            if (listingResult.IsFailure)
            {
                if (listingResult.Error.Type == ErrorType.NotFound)
                {
                    lines.Add(CreateFailedLine(item, RentalCartLineStatus.Unavailable, "Sản phẩm không tồn tại hoặc đã bị xóa."));
                }
                else
                {
                    return Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Không thể kiểm tra thông tin thiết bị lúc này.");
                }
                continue;
            }

            RentalListingSnapshot listing = listingResult.Value;

            // MỚI: Check listing do chính mình sở hữu
            if (listing.OwnerId == renterId)
            {
                lines.Add(CreateFailedLine(item, RentalCartLineStatus.ValidationFailed, "Không thể thuê thiết bị do chính mình sở hữu."));
                continue;
            }

            if (!listing.IsPublished || !listing.AcceptsBookings)
            {
                lines.Add(CreateLine(item, listing, duration, RentalCartLineStatus.ListingDisabled, "Sản phẩm hiện không nhận đơn thuê."));
                continue;
            }

            // MỚI: Kiểm tra PriceVersion từ Client gửi lên so với bản Snapshot hiện tại
            string? clientVersion = clientPriceVersions != null && clientPriceVersions.TryGetValue(item.Id, out var v) ? v : null;
            if (clientVersion != null && clientVersion != listing.PriceVersion)
            {
                lines.Add(CreateLine(item, listing, duration, RentalCartLineStatus.PriceChanged, "Giá hoặc cọc đã thay đổi, vui lòng xác nhận lại."));
                continue;
            }

            Result<int> availResult = await availabilityReader.GetAvailableQuantityAsync(
                item.EquipmentListingId, item.ExpectedStartDate, item.ExpectedEndDate, cancellationToken);

            if (availResult.IsFailure)
            {
                return Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Không thể kiểm tra tồn kho lúc này.");
            }

            int availableQty = availResult.Value;

            if (availableQty < item.Quantity)
            {
                lines.Add(CreateLine(item, listing, duration, RentalCartLineStatus.InsufficientQuantity, $"Chỉ còn {availableQty} sản phẩm khả dụng."));
                continue;
            }

            lines.Add(CreateLine(item, listing, duration, RentalCartLineStatus.Available, null));
        }

        return Result.Success(new RentalCartReadModel(cart.Id, lines));
    }

    private static RentalCartLineReadModel CreateFailedLine(RentalCartItem item, RentalCartLineStatus status, string message)
    {
        return new RentalCartLineReadModel(
            item.Id, item.EquipmentListingId, item.Quantity, item.ExpectedStartDate, item.ExpectedEndDate,
            CurrentDailyRate: null, CurrentDeposit: null, EstimatedRentalAmount: null, CurrentPriceVersion: null, status, message);
    }

    private static RentalCartLineReadModel CreateLine(RentalCartItem item, RentalListingSnapshot listing, int duration, RentalCartLineStatus status, string? message)
    {
        // SỬA: Công thức tính tiền chuẩn xác
        decimal estimatedAmount = duration * listing.DailyRate * item.Quantity;

        return new RentalCartLineReadModel(
            item.Id, item.EquipmentListingId, item.Quantity, item.ExpectedStartDate, item.ExpectedEndDate,
            listing.DailyRate, listing.Deposit, estimatedAmount, listing.PriceVersion, status, message);
    }
}
