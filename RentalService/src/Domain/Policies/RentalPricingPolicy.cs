namespace Dprental.RentalService.Domain.Policies;

/// <summary>
/// Công thức tính số ngày thuê, tiền thuê và tiền cọc dùng chung cho checkout và RentalItem.
/// </summary>
public static class RentalPricingPolicy
{
    /// <summary>
    /// Số ngày thuê của khoảng [expectedStartDate, expectedEndDate) lưu trên đơn hàng, ngày kết thúc không tính.
    /// Khách chọn giao 10, trả 12 (tính cả hai ngày) được checkout lưu thành [10, 13) nên ra 3 ngày.
    /// </summary>
    public static int CalculateRentalDays(DateOnly expectedStartDate, DateOnly expectedEndDate) =>
        expectedEndDate.DayNumber - expectedStartDate.DayNumber;

    public static decimal CalculateRentalAmount(decimal dailyRate, int quantity, int rentalDays) =>
        dailyRate * quantity * rentalDays;

    public static decimal CalculateDepositAmount(decimal depositPerUnit, int quantity) =>
        depositPerUnit * quantity;
}
