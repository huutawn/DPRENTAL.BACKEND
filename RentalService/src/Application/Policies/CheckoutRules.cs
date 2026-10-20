using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Policies;

namespace Dprental.RentalService.Application.Policies;

/// <summary>
/// Quy tắc ngày thuê lúc checkout: đặt trước ít nhất <see cref="RentalBookingPolicy.MinimumLeadDays"/> ngày
/// (SRS cũ ghi "từ ngày mai"), trong 6 tháng, tối đa 30 ngày.
/// Giỏ lưu ngày trả có tính (giao 10, trả 12 là 3 ngày); đơn hàng lưu khoảng [từ, đến) không tính ngày cuối.
/// </summary>
public static class CheckoutRules
{
    public const int MaxRentalDays = 30;
    public const int BookingHorizonMonths = 6;
    public const string Currency = "VND";

    /// <summary>Phase 1 chỉ có một cách giao: nhà cung cấp giao tới địa chỉ của người thuê.</summary>
    public const string DeliveryMethod = "Delivery";

    public const int MaxIdempotencyKeyLength = 64;

    /// <summary>Thời gian chờ thanh toán, cũng là hạn giữ lịch bên Inventory.</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Mã của một lần checkout, lưu vào RentalOrder.CheckoutOperationId. Gắn Idempotency-Key với renter
    /// để hai renter gửi trùng key không đụng nhau.
    /// </summary>
    public static string DeriveOperationId(Guid renterId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return RentalOperationExecutor.ComputeOperationKey("Checkout", $"{renterId}:{idempotencyKey.Trim()}");
    }

    /// <summary>
    /// Cùng renter, cùng lần checkout và cùng dòng giỏ luôn ra cùng một RentalItemId, nên lần gửi lại
    /// dùng lại đúng ExternalReference và Inventory trả về hold cũ thay vì giữ thêm máy.
    /// </summary>
    public static Guid DeriveRentalItemId(Guid renterId, string checkoutOperationId, Guid cartItemId) =>
        Guid.ParseExact(
            RentalOperationExecutor.ComputeOperationKey("CheckoutRentalItem", $"{renterId}:{checkoutOperationId}:{cartItemId}"),
            "N");

    /// <summary>
    /// Mã nhóm máy của một dòng thuê bên Inventory (groupReference, cùng SourceSystem "rental").
    /// Rental lưu mã này vào RentalItem.AllocationGroupId; các lệnh Kafka của Inventory cũng tìm nhóm theo mã này.
    /// </summary>
    public static string HoldGroupReference(Guid rentalItemId) => $"rental-item:{rentalItemId}";

    /// <summary>
    /// Đổi khoảng ngày của dòng giỏ (ngày trả có tính) sang khoảng [từ, đến) của đơn hàng:
    /// giao 10, trả 12 thành [10, 13), tức 3 ngày thuê và Inventory giữ lịch các ngày 10, 11, 12.
    /// </summary>
    public static (DateOnly From, DateOnly To) ToOrderPeriod(RentalCartItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.ExpectedStartDate, item.ExpectedEndDate.AddDays(1));
    }

    public static CheckoutLineIssue? CheckRentalPeriod(RentalCartItem item, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (RentalBookingPolicy.IsStartDateTooSoon(item.ExpectedStartDate, today))
        {
            return new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.StartDateNotInFuture,
                RentalBookingPolicy.TooSoonMessage(today));
        }

        if (item.ExpectedStartDate > today.AddMonths(BookingHorizonMonths))
        {
            return new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.StartDateBeyondHorizon,
                $"Ngày giao dự kiến phải trong vòng {BookingHorizonMonths} tháng tới.");
        }

        (DateOnly from, DateOnly to) = ToOrderPeriod(item);
        if (RentalPricingPolicy.CalculateRentalDays(from, to) > MaxRentalDays)
        {
            return new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.RentalPeriodTooLong,
                $"Thời gian thuê tối đa {MaxRentalDays} ngày.");
        }

        return null;
    }
}
