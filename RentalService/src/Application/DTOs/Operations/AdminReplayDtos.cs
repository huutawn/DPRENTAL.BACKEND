namespace Dprental.RentalService.Application.DTOs.Operations;

/// <summary>
/// DTO yêu cầu thực hiện replay một tác vụ (operation) bởi Quản trị viên (Admin).
/// </summary>
/// <param name="Reason">Lý do thực hiện replay (bắt buộc, tối đa 1000 ký tự) phục vụ kiểm toán.</param>
public sealed record AdminReplayRequest(string Reason);

/// <summary>
/// DTO phản hồi sau khi Quản trị viên (Admin) replay thành công một tác vụ.
/// </summary>
/// <param name="OrderId">Mã định danh duy nhất của đơn thuê liên quan.</param>
/// <param name="OperationId">Mã định danh tác vụ được replay.</param>
/// <param name="Action">Hành động của tác vụ nghiệp vụ.</param>
/// <param name="Status">Trạng thái mới của tác vụ sau khi được đặt lại để chạy lại.</param>
/// <param name="RetryCount">Số lần đã thử lại (được đặt lại về 0).</param>
/// <param name="ReplayedAt">Thời điểm ghi nhận yêu cầu replay.</param>
public sealed record AdminReplayResponse(
    Guid OrderId,
    string OperationId,
    string Action,
    string Status,
    int RetryCount,
    DateTimeOffset ReplayedAt);
