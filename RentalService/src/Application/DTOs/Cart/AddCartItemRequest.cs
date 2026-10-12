namespace Dprental.RentalService.Api.Contracts.Cart;

/// <summary>
/// DTO yêu cầu thêm một mục thiết bị vào giỏ hàng của người thuê.
/// </summary>
/// <param name="EquipmentListingId">Mã định danh duy nhất của bài niêm yết thiết bị (EquipmentListing) muốn thuê.</param>
/// <param name="Quantity">Số lượng thiết bị muốn thuê (phải lớn hơn 0).</param>
/// <param name="ExpectedStartDate">Ngày bắt đầu thuê dự kiến (phải trong tương lai, sau ngày hiện tại).</param>
/// <param name="ExpectedEndDate">Ngày kết thúc thuê dự kiến (phải lớn hơn hoặc bằng ngày bắt đầu và trong giới hạn cho phép).</param>
public sealed record AddCartItemRequest(
    Guid EquipmentListingId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate);
