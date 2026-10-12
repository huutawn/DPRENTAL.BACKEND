using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Policies;

namespace Dprental.RentalService.Application.Checkout;

/// <summary>
/// Quy tắc ngày thuê lúc checkout theo SRS Rental: giao từ ngày mai, trong 6 tháng, tối đa 30 ngày.
/// Khoảng thuê là [ExpectedStartDate, ExpectedEndDate), ngày kết thúc không tính.
/// </summary>
public static class CheckoutRules
{
    public const int MaxRentalDays = 30;
    public const int BookingHorizonMonths = 6;
    public const string Currency = "VND";

    /// <summary>Thời gian chờ thanh toán, cũng là hạn giữ lịch bên Inventory.</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(30);

    public static CheckoutLineIssue? CheckRentalPeriod(RentalCartItem item, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ExpectedStartDate <= today)
        {
            return new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.StartDateNotInFuture,
                "Ngày giao dự kiến phải từ ngày mai trở đi.");
        }

        if (item.ExpectedStartDate > today.AddMonths(BookingHorizonMonths))
        {
            return new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.StartDateBeyondHorizon,
                $"Ngày giao dự kiến phải trong vòng {BookingHorizonMonths} tháng tới.");
        }

        if (RentalPricingPolicy.CalculateRentalDays(item.ExpectedStartDate, item.ExpectedEndDate) > MaxRentalDays)
        {
            return new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.RentalPeriodTooLong,
                $"Thời gian thuê tối đa {MaxRentalDays} ngày.");
        }

        return null;
    }
}
