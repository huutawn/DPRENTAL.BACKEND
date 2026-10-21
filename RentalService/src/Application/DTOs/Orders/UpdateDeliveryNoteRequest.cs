namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Ghi chú giao hàng mới cho nhà cung cấp.
/// </summary>
/// <param name="Note">Nội dung ghi chú (tối đa 500 ký tự). Null hoặc chuỗi rỗng là xóa ghi chú.</param>
public sealed record UpdateDeliveryNoteRequest(string? Note);
