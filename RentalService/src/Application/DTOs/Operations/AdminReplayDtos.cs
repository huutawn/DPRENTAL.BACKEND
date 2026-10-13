namespace Dprental.RentalService.Application.DTOs.Operations;

/// <summary>
/// DTO yêu cầu thực hiện replay một tác vụ (operation) bởi Quản trị viên (Admin).
/// </summary>
/// <param name="Reason">Lý do thực hiện replay (bắt buộc, tối đa 1000 ký tự) phục vụ kiểm toán.</param>
public sealed record AdminReplayRequest(string Reason);
