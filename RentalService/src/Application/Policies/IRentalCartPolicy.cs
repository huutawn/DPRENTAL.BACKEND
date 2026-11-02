namespace Dprental.RentalService.Application.Policies;

/// <remarks>
/// Contract cung cấp giới hạn kỳ thuê cho phần xử lý giỏ; RentalCartOptions là implementation hiện tại.
/// Không thực thi truy vấn hoặc giữ lịch Inventory.
/// </remarks>
public interface IRentalCartPolicy
{
    int MaximumRentalDays { get; }
}
