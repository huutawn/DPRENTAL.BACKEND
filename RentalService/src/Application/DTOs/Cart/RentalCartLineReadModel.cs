namespace Dprental.RentalService.Application.DTOs.Cart;

/// <summary>
/// DTO chi tiết một dòng mục thiết bị trong giỏ hàng.
/// </summary>
/// <param name="CartItemId">Mã định danh duy nhất của mục trong giỏ hàng (CartItemId).</param>
/// <param name="EquipmentListingId">Mã định danh bài niêm yết thiết bị (EquipmentListingId).</param>
/// <param name="Quantity">Số lượng thiết bị muốn thuê.</param>
/// <param name="ExpectedStartDate">Ngày bắt đầu thuê dự kiến.</param>
/// <param name="ExpectedEndDate">Ngày kết thúc thuê dự kiến.</param>
/// <param name="CurrentDailyRate">Đơn giá thuê mỗi ngày hiện tại lấy từ InventoryService.</param>
/// <param name="CurrentDeposit">Tiền đặt cọc mỗi thiết bị hiện tại lấy từ InventoryService.</param>
/// <param name="EstimatedRentalAmount">Tổng tiền thuê ước tính cho khoảng ngày và số lượng đã chọn.</param>
/// <param name="CurrentPriceVersion">Phiên bản giá hiện tại của bài niêm yết thiết bị để đối chiếu.</param>
/// <param name="Status">Trạng thái kiểm tra tính khả dụng và hợp lệ của mục giỏ hàng.</param>
/// <param name="InteractionStatus">Mức thao tác cho phép đối với mục giỏ hàng.</param>
/// <param name="Message">Thông điệp chi tiết giải thích trạng thái nếu có lỗi hoặc cảnh báo.</param>
public sealed record RentalCartLineReadModel(
    Guid CartItemId,
    Guid EquipmentListingId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    decimal? CurrentDailyRate,
    decimal? CurrentDeposit,
    decimal? EstimatedRentalAmount,
    string? CurrentPriceVersion,
    RentalCartLineStatus Status,
    RentalCartItemInteractionStatus InteractionStatus,
    string? Message);
