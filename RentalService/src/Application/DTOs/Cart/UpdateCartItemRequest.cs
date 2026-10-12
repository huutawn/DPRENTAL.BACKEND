namespace Dprental.RentalService.Api.Contracts.Cart;

/// <summary>
/// DTO yêu cầu cập nhật mục trong giỏ hàng.
/// </summary>
/// <remarks>
/// Mỗi yêu cầu PATCH chỉ được phép cập nhật hoặc Quantity, hoặc cả cặp ngày ExpectedStartDate và ExpectedEndDate.
/// </remarks>
/// <param name="Quantity">Số lượng thiết bị mới muốn thuê (phải lớn hơn 0 nếu được cung cấp).</param>
/// <param name="ExpectedStartDate">Ngày bắt đầu thuê mới (phải đi kèm ExpectedEndDate).</param>
/// <param name="ExpectedEndDate">Ngày kết thúc thuê mới (phải đi kèm ExpectedStartDate).</param>
public sealed record UpdateCartItemRequest(
    int? Quantity,
    DateOnly? ExpectedStartDate,
    DateOnly? ExpectedEndDate);
