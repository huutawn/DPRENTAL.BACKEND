using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử đơn vị cho EquipmentAvailabilityPolicy theo các quy tắc nghiệp vụ trong SRS v3.1.
/// Bao gồm kiểm tra thiết bị tắt nhận đặt, hold hết hạn không chiếm lịch, ngày thu hồi D của lượt Completed
/// và tình trạng quá hạn trả (overdue) đối với các lượt đặt bắt đầu hôm nay/ngày mai.
/// </summary>
public sealed class AvailabilityPolicyTests
{
    private readonly Guid _equipmentId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero); // Hôm nay là ngày 2026-10-10 tại VN

    private InventoryEquipment CreateActiveEquipment() =>
        InventoryEquipment.Create(
            _equipmentId,
            _organizationId,
            CatalogLifecycleStatus.Active,
            acceptingBookings: true,
            _now).Value;

    /// <summary>
    /// Kiểm thử từ chối đặt lịch khi thiết bị đang tắt tính năng nhận đặt lịch (AcceptingBookings == false).
    /// </summary>
    [Fact]
    public void CheckAvailability_WhenNotAcceptingBookings_Fails()
    {
        var equipment = InventoryEquipment.Create(
            _equipmentId,
            _organizationId,
            CatalogLifecycleStatus.Active,
            acceptingBookings: false,
            _now,
            "Tạm dừng nhận đặt").Value;

        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        var result = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidate,
            _now,
            existingReservations: [],
            existingBlocks: []);

        Assert.True(result.IsFailure);
        Assert.Equal("EQUIPMENT_NOT_ACCEPTING_BOOKINGS", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử lượt giữ chỗ đã hết hạn (Held quá hạn) không chiếm lịch và cho phép khách mới đặt lịch.
    /// </summary>
    [Fact]
    public void CheckAvailability_ExpiredHold_DoesNotOccupyCalendar()
    {
        var equipment = CreateActiveEquipment();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        // Lượt giữ chỗ đã hết hạn 2 giờ trước
        DateTimeOffset holdExpiredTime = _now.AddHours(-2);
        var expiredHold = Reservation.CreateHold(
            Guid.NewGuid(),
            _equipmentId,
            candidate,
            holdExpiredTime,
            _now.AddHours(-10), sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        var result = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidate,
            _now,
            existingReservations: [expiredHold],
            existingBlocks: []);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Kiểm thử lượt giữ chỗ còn hiệu lực (Held chưa hết hạn) chiếm lịch và gây xung đột với lượt đặt trùng.
    /// </summary>
    [Fact]
    public void CheckAvailability_ActiveHold_OccupiessCalendarAndConflicts()
    {
        var equipment = CreateActiveEquipment();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        // Lượt giữ chỗ còn hạn 5 giờ nữa
        DateTimeOffset holdExpiresAt = _now.AddHours(5);
        var activeHold = Reservation.CreateHold(
            Guid.NewGuid(),
            _equipmentId,
            candidate,
            holdExpiresAt,
            _now, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        var result = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidate,
            _now,
            existingReservations: [activeHold],
            existingBlocks: []);

        Assert.True(result.IsFailure);
        Assert.Equal("EQUIPMENT_UNAVAILABLE", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử quy tắc: Thu hồi ngày D cho đặt lại từ D+1. Availability phải giữ ngày D là ngày bận dù reservation đã Completed.
    /// Khách đặt bắt đầu vào đúng ngày D sẽ bị từ chối; khách đặt từ D+1 sẽ thành công.
    /// </summary>
    [Fact]
    public void CheckAvailability_CompletedReservation_DayDIsBusy_DayDPlusOneIsAvailable()
    {
        var equipment = CreateActiveEquipment();
        DateOnly dayD = new(2026, 10, 10);
        var completedPeriod = new RentalPeriod(new DateOnly(2026, 10, 10), dayD.AddDays(5));

        var completedRes = Reservation.CreateManual(
            Guid.NewGuid(),
            _equipmentId,
            completedPeriod,
            "Renter",
            "0901234567",
            _now.AddDays(-2)).Value;

        completedRes.Deliver("staff", _now.AddDays(-1), "ok", null, _now.AddDays(-1));
        completedRes.Return("staff", _now, EquipmentReturnCondition.Intact, "ok", null, _now); // Completed ngày D

        // 1. Khách mới đặt bao gồm ngày D (ví dụ từ ngày D đến D+3): BỊ TỪ CHỐI vì ngày D vẫn bận
        var candidateOverlappingDayD = new RentalPeriod(dayD, dayD.AddDays(3));
        var resultOverlapD = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidateOverlappingDayD,
            _now,
            existingReservations: [completedRes],
            existingBlocks: []);

        Assert.True(resultOverlapD.IsFailure);
        Assert.Equal("EQUIPMENT_UNAVAILABLE", resultOverlapD.Error.Code);

        // 2. Khách mới đặt bắt đầu từ ngày D+1 (từ 2026-10-16): THÀNH CÔNG
        var candidateStartingDPlusOne = new RentalPeriod(dayD.AddDays(1), dayD.AddDays(4));
        var resultDPlusOne = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidateStartingDPlusOne,
            _now,
            existingReservations: [completedRes],
            existingBlocks: []);

        Assert.True(resultDPlusOne.IsSuccess);
    }

    /// <summary>
    /// Kiểm thử quy tắc Overdue: Thiết bị có reservation đang InUse mà EndDate &lt; hôm nay (quá hạn trả),
    /// thì từ chối các lượt đặt lịch mới bắt đầu vào hôm nay hoặc ngày mai (hôm nay + 1).
    /// </summary>
    [Fact]
    public void CheckAvailability_WhenEquipmentOverdue_BlocksTodayAndTomorrow()
    {
        var equipment = CreateActiveEquipment();

        // Lượt thuê InUse có EndDate là ngày 2026-10-09 (hôm qua, đã quá hạn vì hôm nay là 2026-10-10)
        var overduePeriod = new RentalPeriod(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 9));
        var overdueRes = Reservation.CreateManual(
            Guid.NewGuid(),
            _equipmentId,
            overduePeriod,
            "Renter",
            "0901234567",
            _now.AddDays(-5)).Value;
        overdueRes.Deliver("staff", _now.AddDays(-5), "ok", null, _now.AddDays(-5)); // Đang InUse

        // 1. Đặt lịch bắt đầu hôm nay (2026-10-10): BỊ TỪ CHỐI do overdue
        var candidateToday = new RentalPeriod(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        var resultToday = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidateToday,
            _now,
            existingReservations: [overdueRes],
            existingBlocks: []);

        Assert.True(resultToday.IsFailure);
        Assert.Equal("EQUIPMENT_OVERDUE_UNAVAILABLE", resultToday.Error.Code);

        // 2. Đặt lịch bắt đầu ngày mai (2026-10-11): BỊ TỪ CHỐI do overdue
        var candidateTomorrow = new RentalPeriod(new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 13));
        var resultTomorrow = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidateTomorrow,
            _now,
            existingReservations: [overdueRes],
            existingBlocks: []);

        Assert.True(resultTomorrow.IsFailure);
        Assert.Equal("EQUIPMENT_OVERDUE_UNAVAILABLE", resultTomorrow.Error.Code);

        // 3. Đặt lịch bắt đầu sau ngày mai (ví dụ từ ngày 2026-10-15): Cho phép kiểm tra bình thường
        var candidateLater = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        var resultLater = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidateLater,
            _now,
            existingReservations: [overdueRes],
            existingBlocks: []);

        Assert.True(resultLater.IsSuccess);
    }

    /// <summary>
    /// Kiểm thử khoảng chặn lịch (CalendarBlock) chặn thành công khoảng thời gian tương ứng.
    /// </summary>
    [Fact]
    public void CheckAvailability_OverlappingCalendarBlock_Conflicts()
    {
        var equipment = CreateActiveEquipment();
        var block = CalendarBlock.Create(
            Guid.NewGuid(),
            _equipmentId,
            new DateOnly(2026, 10, 14),
            new DateOnly(2026, 10, 20),
            CalendarBlockReason.Maintenance,
            "admin",
            _now,
            new DateOnly(2026, 10, 10)).Value;

        var candidate = new RentalPeriod(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 22));

        var result = EquipmentAvailabilityPolicy.CheckAvailability(
            equipment,
            candidate,
            _now,
            existingReservations: [],
            existingBlocks: [block]);

        Assert.True(result.IsFailure);
        Assert.Equal("EQUIPMENT_BLOCKED", result.Error.Code);
    }
}
