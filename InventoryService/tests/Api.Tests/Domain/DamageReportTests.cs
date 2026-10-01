using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử đơn vị cho thực thể DamageReport theo các quy tắc nghiệp vụ trong SRS v3.1.
/// Báo cáo hư hỏng chỉ hợp lệ khi lượt đặt đang InUse, đúng tài khoản khách thuê, tối đa 5 ảnh và tối đa 5 báo cáo trên cùng lượt đặt.
/// </summary>
public sealed class DamageReportTests
{
    private readonly Guid _equipmentId = Guid.NewGuid();
    private readonly Guid _renterAccountId = Guid.NewGuid();
    private readonly DateTimeOffset _baseNow = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly RentalPeriod _validPeriod = new(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

    private Reservation CreateInUseReservation()
    {
        var res = Reservation.CreateHold(
            Guid.NewGuid(),
            _equipmentId,
            _validPeriod,
            _baseNow.AddHours(5),
            _baseNow,
            "web",
            "EXT-DMG-01",
            _renterAccountId).Value;

        res.ConfirmHold(_baseNow.AddHours(1));
        res.Deliver("staff_01", _baseNow.AddDays(4), "Good", null, _baseNow.AddDays(4));
        return res;
    }

    /// <summary>
    /// Kiểm thử tạo báo cáo hư hỏng thành công khi đáp ứng đầy đủ điều kiện.
    /// </summary>
    [Fact]
    public void Create_ValidConditions_Succeeds()
    {
        var res = CreateInUseReservation();
        var photos = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var result = DamageReport.Create(
            id: Guid.NewGuid(),
            reservation: res,
            reporterAccountId: _renterAccountId,
            description: "Mặt kính ống kính bị trầy xước nhẹ",
            isUsable: true,
            photoMediaIds: photos,
            now: _baseNow.AddDays(5),
            existingReportsCountOnReservation: 0);

        Assert.True(result.IsSuccess);
        var report = result.Value;
        Assert.Equal(res.Id, report.ReservationId);
        Assert.Equal(_renterAccountId, report.ReporterAccountId);
        Assert.True(report.IsUsable);
        Assert.Equal(2, report.PhotoMediaIds.Count);
        Assert.Null(report.ReviewedAt);
    }

    /// <summary>
    /// Kiểm thử từ chối báo hỏng khi reservation chưa ở trạng thái InUse (ví dụ còn Confirmed).
    /// </summary>
    [Fact]
    public void Create_ReservationNotYetInUse_Fails()
    {
        var res = Reservation.CreateManual(
            Guid.NewGuid(),
            _equipmentId,
            _validPeriod,
            "Renter",
            "0901234567",
            _baseNow,
            _renterAccountId).Value; // Confirmed, chưa bàn giao

        var result = DamageReport.Create(
            id: Guid.NewGuid(),
            reservation: res,
            reporterAccountId: _renterAccountId,
            description: "Thiết bị bị hỏng hóc",
            isUsable: false,
            photoMediaIds: null,
            now: _baseNow,
            existingReportsCountOnReservation: 0);

        Assert.True(result.IsFailure);
        Assert.Equal("RESERVATION_NOT_IN_USE", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử từ chối báo hỏng khi tài khoản người báo không khớp với khách thuê của reservation.
    /// </summary>
    [Fact]
    public void Create_DifferentReporterAccount_Fails()
    {
        var res = CreateInUseReservation();
        Guid otherAccount = Guid.NewGuid();

        var result = DamageReport.Create(
            id: Guid.NewGuid(),
            reservation: res,
            reporterAccountId: otherAccount, // Khác renter đã liên kết
            description: "Thiết bị bị hỏng hóc",
            isUsable: false,
            photoMediaIds: null,
            now: _baseNow.AddDays(5),
            existingReportsCountOnReservation: 0);

        Assert.True(result.IsFailure);
        Assert.Equal("NOT_RESERVATION_RENTER", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử từ chối báo hỏng khi đính kèm quá 5 ảnh minh chứng.
    /// </summary>
    [Fact]
    public void Create_ExceedsFivePhotos_Fails()
    {
        var res = CreateInUseReservation();
        var sixPhotos = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        var result = DamageReport.Create(
            id: Guid.NewGuid(),
            reservation: res,
            reporterAccountId: _renterAccountId,
            description: "Hỏng hóc nhiều chi tiết",
            isUsable: false,
            photoMediaIds: sixPhotos,
            now: _baseNow.AddDays(5),
            existingReportsCountOnReservation: 0);

        Assert.True(result.IsFailure);
        Assert.Equal("TOO_MANY_PHOTOS", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử từ chối khi lượt đặt đã đạt giới hạn tối đa 5 báo cáo hư hỏng.
    /// </summary>
    [Fact]
    public void Create_ExceedsReservationReportLimit_Fails()
    {
        var res = CreateInUseReservation();

        var result = DamageReport.Create(
            id: Guid.NewGuid(),
            reservation: res,
            reporterAccountId: _renterAccountId,
            description: "Báo cáo thứ 6",
            isUsable: false,
            photoMediaIds: null,
            now: _baseNow.AddDays(5),
            existingReportsCountOnReservation: 5); // Đã có 5 báo cáo trước đó

        Assert.True(result.IsFailure);
        Assert.Equal("DAMAGE_REPORT_LIMIT_EXCEEDED", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử thao tác xem xét (Review) báo cáo hư hỏng từ phía quản trị viên tổ chức.
    /// </summary>
    [Fact]
    public void Review_SetsReviewerAndTimestamp()
    {
        var res = CreateInUseReservation();
        var report = DamageReport.Create(
            id: Guid.NewGuid(),
            reservation: res,
            reporterAccountId: _renterAccountId,
            description: "Thiết bị bị hỏng hóc",
            isUsable: true,
            photoMediaIds: null,
            now: _baseNow.AddDays(5),
            existingReportsCountOnReservation: 0).Value;

        DateTimeOffset reviewTime = _baseNow.AddDays(6);
        var reviewResult = report.Review("manager_01", reviewTime);

        Assert.True(reviewResult.IsSuccess);
        Assert.Equal("manager_01", report.ReviewedBy);
        Assert.Equal(reviewTime, report.ReviewedAt);
    }
}
