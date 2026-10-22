using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Common.Utils;

namespace Dprental.RentalService.Application.Services.Cart;

/// <summary>
/// Chuyển đổi contract của InventoryService sang nhu cầu của Cart.
/// Chuẩn hóa toàn bộ lỗi dependency và bảo vệ dữ liệu response.
/// </summary>
public sealed class InventoryRentalCartReader(IInventoryRentalClient inventory) :
    IRentalListingReader,
    IRentalAvailabilityReader
{
    public async Task<Result<RentalListingSnapshot>> GetListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        Result<ListingRentalSnapshotsResponse> result = await inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest([listingId]), cancellationToken);

        if (result.IsFailure)
        {
            return NormalizeDependencyError(result.Error);
        }

        if (result.Value is null || result.Value.Items is null || result.Value.MissingIds is null)
        {
            return InvalidInventoryContract();
        }

        ListingRentalSnapshotDto[] matches = result.Value.Items.Where(x => x is not null && x.Id == listingId).Take(2).ToArray();
        if (matches.Length == 0 && result.Value.MissingIds.Contains(listingId))
        {
            return ListingNotFound();
        }

        if (matches.Length != 1 || result.Value.MissingIds.Contains(listingId))
        {
            return InvalidInventoryContract();
        }

        ListingRentalSnapshotDto snapshot = matches[0];

        if (snapshot.DepositPerUnit is not { } deposit ||
            VndAmountValidator.ValidateVndAmount(deposit).IsFailure ||
            VndAmountValidator.ValidateVndAmount(snapshot.DailyRate).IsFailure ||
            !string.Equals(snapshot.Currency, CheckoutRules.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return InvalidInventoryContract();
        }

        bool active = string.Equals(snapshot.Status, "Active", StringComparison.OrdinalIgnoreCase);

        return Result.Success(new RentalListingSnapshot(
            snapshot.Id,
            snapshot.OwnerUserId,
            snapshot.EquipmentTypeId,
            active,
            active,
            snapshot.DailyRate,
            deposit,
            $"{snapshot.DailyRate.ToString(CultureInfo.InvariantCulture)}:{deposit.ToString(CultureInfo.InvariantCulture)}"));
    }

    public async Task<Result<RentalAvailabilityResult>> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        int requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATES", "Ngày bắt đầu không được lớn hơn ngày kết thúc.");
        }

        if (requestedQuantity <= 0)
        {
            return Error.Validation("INVALID_QUANTITY", "Số lượng yêu cầu phải lớn hơn 0.");
        }

        Result<ListingAvailabilityCheckResponse> result = await inventory.CheckListingAvailabilityAsync(
            new ListingAvailabilityCheckRequest(listingId, startDate, endDate, requestedQuantity),
            cancellationToken);

        if (result.IsFailure)
        {
            return NormalizeDependencyError(result.Error);
        }

        ListingAvailabilityCheckResponse? res = result.Value;
        if (res is null ||
            res.ListingId != listingId ||
            res.StartDate != startDate ||
            res.EndDate != endDate ||
            res.RequestedQuantity != requestedQuantity ||
            res.AvailableQuantity < 0)
        {
            return InvalidInventoryContract();
        }

        return Result.Success(new RentalAvailabilityResult(res.AvailableQuantity, res.IsAvailable));
    }

    /// <summary>
    /// Chuẩn hóa lỗi từ Inventory client thành lỗi dependency an toàn, không làm lộ thông tin nội bộ.
    /// </summary>
    private static Error NormalizeDependencyError(Error upstreamError)
    {
        if (upstreamError.Type == ErrorType.NotFound)
        {
            return ListingNotFound();
        }

        return InventoryUnavailable();
    }

    private static Error ListingNotFound() =>
        Error.NotFound("LISTING_NOT_FOUND", "Sản phẩm không tồn tại hoặc đã bị xóa.");

    private static Error InventoryUnavailable() =>
        Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.");

    private static Error InvalidInventoryContract() =>
        Error.ServiceUnavailable("INVENTORY_CONTRACT_INVALID", "Không thể xác minh dữ liệu thiết bị lúc này.");
}
