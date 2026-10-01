using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử toàn diện cho State Machine của Reservation theo đặc tả SRS v3.1.
/// Bao gồm kiểm tra toàn bộ các transitions hợp lệ và không hợp lệ, idempotency retry,
/// kiểm soát hạn hold, giới hạn độ dài lý do hủy và tính bất biến của thông tin giao/thu hồi.
/// </summary>
public sealed class ReservationStateMachineTests
{
    private readonly Guid _equipmentId = Guid.NewGuid();
    private readonly DateTimeOffset _baseNow = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly RentalPeriod _validPeriod = new(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

    /// <summary>
    /// Kiểm thử tạo hold hợp lệ với thời hạn trước 00:00 ngày bắt đầu theo giờ Việt Nam.
    /// </summary>
    [Fact]
    public void CreateHold_ValidData_SetsHeldStatusAndTransition()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(24); // Trước ngày 2026-10-05 00:00 ICT

        var result = Reservation.CreateHold(
            id: Guid.NewGuid(),
            equipmentId: _equipmentId,
            period: _validPeriod,
            holdExpiresAt: holdExpiresAt,
            now: _baseNow,
            sourceSystem: "rental_portal",
            externalReference: "EXT-001", renterAccountId: Guid.NewGuid());

        Assert.True(result.IsSuccess);
        var res = result.Value;
        Assert.Equal(ReservationStatus.Held, res.Status);
        Assert.Equal(holdExpiresAt, res.HoldExpiresAt);
        Assert.Equal(ReservationSourceType.External, res.SourceType);
        Assert.Single(res.Transitions);

        var transition = res.Transitions.First();
        Assert.Equal(ReservationOperationType.CreateHold, transition.OperationType);
        Assert.Null(transition.FromStatus);
        Assert.Equal(ReservationStatus.Held, transition.ToStatus);
        Assert.Equal(1, transition.Sequence);
    }

    /// <summary>
    /// Kiểm thử tạo hold với thời hạn trong quá khứ bị từ chối.
    /// </summary>
    [Fact]
    public void CreateHold_HoldExpiresInPast_Fails()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(-1);

        var result = Reservation.CreateHold(
            id: Guid.NewGuid(),
            equipmentId: _equipmentId,
            period: _validPeriod,
            holdExpiresAt: holdExpiresAt,
            now: _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_IN_PAST", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử tạo hold với thời hạn vượt quá 00:00 ngày bắt đầu bị từ chối.
    /// </summary>
    [Fact]
    public void CreateHold_HoldExpiresAfterStartDate_Fails()
    {
        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(_validPeriod.StartDate);
        DateTimeOffset holdExpiresAt = startOfDayUtc.AddHours(1); // Sau 00:00 giờ VN

        var result = Reservation.CreateHold(
            id: Guid.NewGuid(),
            equipmentId: _equipmentId,
            period: _validPeriod,
            holdExpiresAt: holdExpiresAt,
            now: _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_AFTER_START", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử tạo đặt lịch thủ công thành công, trực tiếp ở trạng thái Confirmed.
    /// </summary>
    [Fact]
    public void CreateManual_ValidData_SetsConfirmedStatus()
    {
        var result = Reservation.CreateManual(
            id: Guid.NewGuid(),
            equipmentId: _equipmentId,
            period: _validPeriod,
            renterName: "Nguyen Van A",
            renterPhone: "0901234567",
            now: _baseNow);

        Assert.True(result.IsSuccess);
        var res = result.Value;
        Assert.Equal(ReservationStatus.Confirmed, res.Status);
        Assert.Equal(ReservationSourceType.Manual, res.SourceType);
        Assert.Null(res.HoldExpiresAt);
        Assert.Single(res.Transitions);
        Assert.Equal(ReservationOperationType.CreateManual, res.Transitions.First().OperationType);
    }

    /// <summary>
    /// Kiểm thử xác nhận hold còn hạn chuyển trạng thái Held -> Confirmed.
    /// </summary>
    [Fact]
    public void ConfirmHold_ActiveHold_TransitionsToConfirmed()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(2);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        DateTimeOffset confirmTime = _baseNow.AddHours(1);
        var result = res.ConfirmHold(confirmTime, "renter");

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Confirmed, res.Status);
        Assert.Equal(2, res.Transitions.Count);

        var transition = res.Transitions.Last();
        Assert.Equal(ReservationOperationType.ConfirmHold, transition.OperationType);
        Assert.Equal(ReservationStatus.Held, transition.FromStatus);
        Assert.Equal(ReservationStatus.Confirmed, transition.ToStatus);
        Assert.Equal(2, transition.Sequence);
    }

    /// <summary>
    /// Kiểm thử xác nhận hold đã hết hạn bị từ chối và không thể xác nhận.
    /// </summary>
    [Fact]
    public void ConfirmHold_ExpiredHold_Fails()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(2);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        DateTimeOffset confirmTime = _baseNow.AddHours(3); // Quá hạn
        var result = res.ConfirmHold(confirmTime, "renter");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRED", result.Error.Code);
        Assert.Equal(ReservationStatus.Held, res.Status);
    }

    /// <summary>
    /// Kiểm thử gia hạn hold thành công tăng thời hạn giữ chỗ (Held -> Held).
    /// </summary>
    [Fact]
    public void ExtendHold_ValidNewExpiry_Succeeds()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(2);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        DateTimeOffset newExpiresAt = _baseNow.AddHours(4);
        var result = res.ExtendHold(newExpiresAt, _baseNow.AddHours(1), "renter");

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Held, res.Status);
        Assert.Equal(newExpiresAt, res.HoldExpiresAt);
        Assert.Equal(2, res.Transitions.Count);

        var transition = res.Transitions.Last();
        Assert.Equal(ReservationOperationType.ExtendHold, transition.OperationType);
        Assert.Equal(holdExpiresAt, transition.PreviousHoldExpiresAt);
        Assert.Equal(newExpiresAt, transition.NewHoldExpiresAt);
    }

    /// <summary>
    /// Kiểm thử gia hạn hold không thể hồi sinh hold đã hết hạn.
    /// </summary>
    [Fact]
    public void ExtendHold_AlreadyExpired_Fails()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(2);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        DateTimeOffset newExpiresAt = _baseNow.AddHours(4);
        var result = res.ExtendHold(newExpiresAt, _baseNow.AddHours(3), "renter"); // Đã hết hạn

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRED", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử nhả hold chủ động chuyển sang trạng thái Released kèm lý do.
    /// </summary>
    [Fact]
    public void ReleaseHold_WithReason_TransitionsToReleased()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(2);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        var result = res.ReleaseHold("Khách đổi ý không thuê nữa", _baseNow.AddHours(1), "renter");

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Released, res.Status);
        Assert.True(res.IsTerminalState());
    }

    /// <summary>
    /// Kiểm thử xử lý tự động hết hạn hold chuyển trạng thái Held -> Released.
    /// </summary>
    [Fact]
    public void ExpireHold_PastExpiryTime_TransitionsToReleased()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(2);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        var result = res.ExpireHold(_baseNow.AddHours(3), "system");

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Released, res.Status);
        Assert.True(res.IsTerminalState());
    }

    /// <summary>
    /// Kiểm thử hủy trước giao (Confirmed -> Cancelled) và kiểm soát độ dài lý do hủy theo nguồn.
    /// </summary>
    [Fact]
    public void Cancel_ReasonLengthLimits_EnforcedCorrectly()
    {
        // 1. Nguồn External: giới hạn tối đa 200 ký tự
        var extRes = Reservation.CreateHold(
            Guid.NewGuid(),
            _equipmentId,
            _validPeriod,
            _baseNow.AddHours(10),
            _baseNow,
            "web",
            "EXT-100", renterAccountId: Guid.NewGuid()).Value;
        extRes.ConfirmHold(_baseNow.AddHours(1));

        string longReason201 = new('A', 201);
        var failExt = extRes.Cancel(longReason201, _baseNow.AddHours(2), "system");
        Assert.True(failExt.IsFailure);
        Assert.Equal("REASON_TOO_LONG", failExt.Error.Code);

        string validReason200 = new('A', 200);
        var okExt = extRes.Cancel(validReason200, _baseNow.AddHours(2), "system");
        Assert.True(okExt.IsSuccess);
        Assert.Equal(ReservationStatus.Cancelled, extRes.Status);

        // 2. Nguồn Manual: giới hạn tối đa 500 ký tự
        var manRes = Reservation.CreateManual(
            Guid.NewGuid(),
            _equipmentId,
            _validPeriod,
            "Nguyen Van B",
            "0909999999",
            _baseNow).Value;

        string longReason501 = new('B', 501);
        var failMan = manRes.Cancel(longReason501, _baseNow.AddHours(2), "operator");
        Assert.True(failMan.IsFailure);
        Assert.Equal("REASON_TOO_LONG", failMan.Error.Code);

        string validReason500 = new('B', 500);
        var okMan = manRes.Cancel(validReason500, _baseNow.AddHours(2), "operator");
        Assert.True(okMan.IsSuccess);
        Assert.Equal(ReservationStatus.Cancelled, manRes.Status);
    }

    /// <summary>
    /// Kiểm thử quy trình Bàn giao (Confirmed -> InUse) và Thu hồi (InUse -> Completed) cùng tính bất biến sau khi ghi.
    /// </summary>
    [Fact]
    public void DeliverAndReturn_FullLifecycle_MaintainsImmutability()
    {
        var res = Reservation.CreateManual(
            Guid.NewGuid(),
            _equipmentId,
            _validPeriod,
            "Nguyen Van C",
            "0908888888",
            _baseNow).Value;

        // Bàn giao thiết bị
        var deliverPhotos = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        DateTimeOffset deliveredAt = _baseNow.AddDays(4);
        var deliverResult = res.Deliver(
            actor: "staff_01",
            deliveredAt: deliveredAt,
            conditionNotes: "Thiết bị nguyên vẹn, đầy đủ phụ kiện",
            photoIds: deliverPhotos,
            now: deliveredAt);

        Assert.True(deliverResult.IsSuccess);
        Assert.Equal(ReservationStatus.InUse, res.Status);
        Assert.Equal(deliveredAt, res.DeliveredAt);
        Assert.Equal("staff_01", res.DeliveredBy);
        Assert.Equal(2, res.DeliveryPhotoIds.Count);

        // Không cho phép bàn giao lại (bất biến)
        var secondDeliver = res.Deliver("staff_02", deliveredAt, "note", null, deliveredAt);
        Assert.True(secondDeliver.IsFailure);

        // Thu hồi thiết bị
        var returnPhotos = new List<Guid> { Guid.NewGuid() };
        DateTimeOffset returnedAt = _baseNow.AddDays(9);
        var returnResult = res.Return(
            actor: "staff_03",
            returnedAt: returnedAt,
            conditionNotes: "Hoàn trả đầy đủ, thiết bị hoạt động tốt",
            photoIds: returnPhotos,
            now: returnedAt, condition: EquipmentReturnCondition.Intact);

        Assert.True(returnResult.IsSuccess);
        Assert.Equal(ReservationStatus.Completed, res.Status);
        Assert.Equal(returnedAt, res.ReturnedAt);
        Assert.Equal("staff_03", res.ReturnedBy);
        Assert.Single(res.ReturnPhotoIds);
        Assert.True(res.IsTerminalState());

        // Không cho phép thu hồi lại (bất biến)
        var secondReturn = res.Return("staff_04", returnedAt, EquipmentReturnCondition.Intact, "note", null, returnedAt);
        Assert.True(secondReturn.IsFailure);
    }

    /// <summary>
    /// Kiểm thử cơ chế Idempotency: thực hiện lại thao tác với cùng OperationKey không tạo thêm transition và trả về thành công.
    /// </summary>
    [Fact]
    public void OperationKey_RetryIdempotency_DoesNotCreateDuplicateTransitions()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(5);
        var res = Reservation.CreateHold(Guid.NewGuid(), _equipmentId, _validPeriod, holdExpiresAt, _baseNow, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;

        const string operationKey = "OP-CONFIRM-12345";

        // Lần gọi đầu tiên
        var firstCall = res.ConfirmHold(_baseNow.AddHours(1), "renter", operationKey);
        Assert.True(firstCall.IsSuccess);
        Assert.Equal(ReservationStatus.Confirmed, res.Status);
        int transitionsCountAfterFirst = res.Transitions.Count;

        // Lần gọi lại (retry) với cùng operationKey
        var retryCall = res.ConfirmHold(_baseNow.AddHours(2), "renter", operationKey);
        Assert.True(retryCall.IsSuccess);
        Assert.Equal(ReservationStatus.Confirmed, res.Status);
        // Không phát sinh thêm transition mới trong lịch sử
        Assert.Equal(transitionsCountAfterFirst, res.Transitions.Count);
    }
}
