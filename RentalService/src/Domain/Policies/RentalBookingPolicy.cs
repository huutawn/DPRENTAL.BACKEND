namespace Dprental.RentalService.Domain.Policies;

/// <summary>
/// Thời gian đặt trước tối thiểu, dùng chung cho giỏ hàng và checkout.
/// Hôm nay 14/10 (giờ Việt Nam) thì ngày bắt đầu thuê sớm nhất là 17/10.
/// </summary>
public static class RentalBookingPolicy
{
    public const int MinimumLeadDays = 3;

    public static DateOnly EarliestStartDate(DateOnly today) => today.AddDays(MinimumLeadDays);

    public static bool IsStartDateTooSoon(DateOnly startDate, DateOnly today) => startDate < EarliestStartDate(today);

    public static string TooSoonMessage(DateOnly today) =>
        $"Phải đặt trước ít nhất {MinimumLeadDays} ngày, ngày bắt đầu thuê sớm nhất là {EarliestStartDate(today):dd/MM/yyyy}.";
}
