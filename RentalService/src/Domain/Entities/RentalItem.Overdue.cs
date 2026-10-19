using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    public const string OverdueProtectionAction = "MarkAssetsOverdue";

    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    /// 00:00 giờ VN của ngày sau ngày trả dự kiến.
    public static DateTimeOffset CalculateOverdueAt(DateOnly expectedEndDate)
    {
        DateOnly next = expectedEndDate.AddDays(1);
        return new DateTimeOffset(next.Year, next.Month, next.Day, 0, 0, 0, VietnamOffset);
    }

    public static DateOnly TodayInVietnam(DateTimeOffset now) =>
        DateOnly.FromDateTime(now.ToOffset(VietnamOffset).DateTime);

    /// 00:00 giờ VN của ngày sau ngày bắt đầu dự kiến (mốc quá hạn giao máy).
    public static DateTimeOffset CalculateHandoverOverdueAt(DateOnly expectedStartDate) =>
        CalculateOverdueAt(expectedStartDate);

    public bool IsHandoverOverdue(DateTimeOffset now) =>
        Status == RentalItemStatus.AwaitingHandover && now >= CalculateHandoverOverdueAt(ExpectedStartDate);

    public bool IsHandoverAutoCancellable(DateTimeOffset now) =>
        Status == RentalItemStatus.AwaitingHandover && now >= CalculateOverdueAt(ExpectedEndDate);

    public bool IsOverdue(DateTimeOffset now) =>
        Status == RentalItemStatus.InRental && now >= CalculateOverdueAt(ExpectedEndDate);

    /// Máy đã giao nhưng chưa ghi nhận trả.
    public IReadOnlyList<RentalUnitAssignment> GetUnreturnedAssignments() =>
        _unitAssignments.Where(u => u.Status == RentalUnitAssignmentStatus.HandedOver).ToList();
}