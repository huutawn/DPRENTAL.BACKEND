using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.Policies;

namespace Dprental.RentalService.Application.Services.Cart;

/// <summary>
/// Kết quả đánh giá tính khả dụng và quyền thao tác trên một dòng giỏ hàng.
/// </summary>
/// <param name="Snapshot">Thông tin bài niêm yết thiết bị nếu đọc thành công và còn hiệu lực.</param>
/// <param name="Status">Trạng thái nguyên nhân của dòng giỏ hàng.</param>
/// <param name="InteractionStatus">Mức thao tác cho phép (ENABLED, DISABLED_EDITABLE, DISABLED_READ_ONLY).</param>
/// <param name="Message">Thông điệp giải thích nếu có cảnh báo hoặc lỗi.</param>
/// <param name="AvailableQuantity">Số lượng sản phẩm khả dụng nếu đã kiểm tra tồn kho.</param>
/// <param name="ErrorCode">Mã lỗi chi tiết từ bước xác thực nếu có.</param>
public sealed record RentalCartItemEvaluationResult(
    RentalListingSnapshot? Snapshot,
    RentalCartLineStatus Status,
    RentalCartItemInteractionStatus InteractionStatus,
    string? Message,
    int? AvailableQuantity = null,
    string? ErrorCode = null);

/// <summary>
/// Thành phần duy nhất chịu trách nhiệm đánh giá tính hợp lệ, trạng thái và khả năng thuê của cart item.
/// Được dùng chung bởi GetRentalCart và RentalCartUseCases.
/// </summary>
public sealed class RentalCartItemEvaluator(
    IRentalListingReader listingReader,
    IRentalAvailabilityReader availabilityReader,
    IRentalCartPolicy policy,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Kiểm tra tính hợp lệ của cặp ngày thuê theo chính sách chung.
    /// Thứ tự: kiểm tra lỗi kỳ thuê (đảo ngược, quá hạn policy, quá 6 tháng) trước mốc quá hạn đặt trước 3 ngày.
    /// </summary>
    public Result ValidateRentalDates(DateOnly startDate, DateOnly endDate)
    {
        DateOnly todayVn = DateOnly.FromDateTime(timeProvider.GetUtcNow().AddHours(7).DateTime);

        if (startDate > endDate)
        {
            return RentalErrors.InvalidRentalPeriod;
        }

        int durationDays = endDate.DayNumber - startDate.DayNumber + 1;
        if (durationDays > policy.MaximumRentalDays)
        {
            return RentalErrors.DurationExceedsOneYear;
        }

        if (startDate > todayVn.AddMonths(6))
        {
            return Error.Validation("Cart.AdvanceBookingTooFar", "Chỉ được phép đặt trước tối đa 6 tháng.");
        }

        if (RentalBookingPolicy.IsStartDateTooSoon(startDate, todayVn))
        {
            return Error.Validation("Cart.StartDateInvalid", RentalBookingPolicy.TooSoonMessage(todayVn));
        }

        return Result.Success();
    }

    /// <summary>
    /// Đánh giá đầy đủ một dòng giỏ hàng theo thứ tự ưu tiên:
    /// 1. Listing tồn tại và nhận thuê.
    /// 2. Quyền sở hữu (không thuê của chính mình).
    /// 3. Số lượng và kỳ thuê (dùng ValidateRentalDates).
    /// 4. Availability tồn kho (xử lý race condition nếu availability trả NotFound).
    /// </summary>
    public async Task<Result<RentalCartItemEvaluationResult>> EvaluateItemAsync(
        Guid renterId,
        Guid listingId,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra listing tồn tại và đang nhận thuê (Ưu tiên hơn lỗi ngày!)
        Result<RentalListingSnapshot> listingResult = await listingReader.GetListingAsync(listingId, cancellationToken);
        if (listingResult.IsFailure)
        {
            if (listingResult.Error.Type == ErrorType.NotFound)
            {
                return Result.Success(new RentalCartItemEvaluationResult(
                    null,
                    RentalCartLineStatus.Unavailable,
                    RentalCartItemInteractionStatus.DisabledReadOnly,
                    "Sản phẩm không tồn tại hoặc đã bị xóa."));
            }
            return listingResult.Error;
        }

        RentalListingSnapshot listing = listingResult.Value;
        if (!listing.IsPublished || !listing.AcceptsBookings)
        {
            return Result.Success(new RentalCartItemEvaluationResult(
                listing,
                RentalCartLineStatus.ListingDisabled,
                RentalCartItemInteractionStatus.DisabledReadOnly,
                "Sản phẩm hiện không nhận đơn thuê."));
        }

        // 2. Kiểm tra ownership
        if (listing.OwnerId == renterId)
        {
            return Result.Success(new RentalCartItemEvaluationResult(
                listing,
                RentalCartLineStatus.ValidationFailed,
                RentalCartItemInteractionStatus.DisabledReadOnly,
                "Không thể thuê thiết bị do chính mình sở hữu."));
        }

        // 3. Kiểm tra số lượng và kỳ thuê bằng validation dùng chung
        if (quantity < 1)
        {
            return Result.Success(new RentalCartItemEvaluationResult(
                listing,
                RentalCartLineStatus.ValidationFailed,
                RentalCartItemInteractionStatus.DisabledEditable,
                "Số lượng thuê phải lớn hơn 0."));
        }

        Result dateCheck = ValidateRentalDates(startDate, endDate);
        if (dateCheck.IsFailure)
        {
            if (dateCheck.Error.Code == "Cart.StartDateInvalid")
            {
                return Result.Success(new RentalCartItemEvaluationResult(
                    listing,
                    RentalCartLineStatus.DatePassed,
                    RentalCartItemInteractionStatus.DisabledEditable,
                    dateCheck.Error.Message,
                    ErrorCode: dateCheck.Error.Code));
            }

            return Result.Success(new RentalCartItemEvaluationResult(
                listing,
                RentalCartLineStatus.ValidationFailed,
                RentalCartItemInteractionStatus.DisabledEditable,
                dateCheck.Error.Message,
                ErrorCode: dateCheck.Error.Code));
        }

        // 4. Kiểm tra availability (chỉ gọi khi listing và ngày đều hợp lệ)
        Result<RentalAvailabilityResult> availResult = await availabilityReader.GetAvailableQuantityAsync(
            listingId, startDate, endDate, quantity, cancellationToken);

        if (availResult.IsFailure)
        {
            // Race condition: Snapshot đọc thành công nhưng availability trả NotFound (listing vừa bị xóa)
            if (availResult.Error.Type == ErrorType.NotFound)
            {
                return Result.Success(new RentalCartItemEvaluationResult(
                    null,
                    RentalCartLineStatus.Unavailable,
                    RentalCartItemInteractionStatus.DisabledReadOnly,
                    "Sản phẩm không tồn tại hoặc đã bị xóa."));
            }

            return availResult.Error;
        }

        RentalAvailabilityResult avail = availResult.Value;
        if (!avail.IsAvailable || avail.AvailableQuantity < quantity)
        {
            return Result.Success(new RentalCartItemEvaluationResult(
                listing,
                RentalCartLineStatus.InsufficientQuantity,
                RentalCartItemInteractionStatus.DisabledEditable,
                $"Chỉ còn {avail.AvailableQuantity} sản phẩm khả dụng.",
                avail.AvailableQuantity));
        }

        // 5. Tất cả kiểm tra đạt
        return Result.Success(new RentalCartItemEvaluationResult(
            listing,
            RentalCartLineStatus.Available,
            RentalCartItemInteractionStatus.Enabled,
            null,
            avail.AvailableQuantity));
    }

    /// <summary>
    /// Dựng DTO RentalCartLineReadModel từ thông tin item và kết quả đánh giá.
    /// Với listing còn đọc được và kỳ thuê hợp lệ, giữ cách tính giá ước tính hiện tại.
    /// Không đủ dữ liệu thì trả các trường giá nullable.
    /// </summary>
    public RentalCartLineReadModel ToLineReadModel(
        Guid cartItemId,
        Guid equipmentListingId,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        RentalCartItemEvaluationResult evaluation)
    {
        RentalListingSnapshot? listing = evaluation.Snapshot;
        bool hasValidPeriod = startDate <= endDate && quantity >= 1 && (endDate.DayNumber - startDate.DayNumber + 1) <= policy.MaximumRentalDays;

        if (listing is not null && hasValidPeriod)
        {
            int duration = endDate.DayNumber - startDate.DayNumber + 1;
            decimal estimatedAmount = duration * listing.DailyRate * quantity;

            return new RentalCartLineReadModel(
                cartItemId,
                equipmentListingId,
                quantity,
                startDate,
                endDate,
                listing.DailyRate,
                listing.Deposit,
                estimatedAmount,
                listing.PriceVersion,
                evaluation.Status,
                evaluation.InteractionStatus,
                evaluation.Message);
        }

        return new RentalCartLineReadModel(
            cartItemId,
            equipmentListingId,
            quantity,
            startDate,
            endDate,
            CurrentDailyRate: null,
            CurrentDeposit: null,
            EstimatedRentalAmount: null,
            CurrentPriceVersion: null,
            evaluation.Status,
            evaluation.InteractionStatus,
            evaluation.Message);
    }
}
