
namespace Dprental.RentalService.Application.DTOs.Calendar;

/// <summary>
/// Thống kê chi tiết một ngày trong lịch thuê của người thuê.
/// </summary>
/// <param name="Date">Ngày trong tháng.</param>
/// <param name="RentedQuantity">Tổng số lượng thiết bị đang thuê trong ngày.</param>
public sealed record RenterCalendarDayResponse(
    DateOnly Date,
    int RentedQuantity
);

/// <summary>
/// Chi tiết một mục thuê trong danh sách lịch thuê của người thuê.
/// Tuyệt đối không để lộ physical asset ID, serial number hoặc allocation reference của Supplier.
/// </summary>
/// <param name="OrderId">Mã định danh duy nhất của đơn thuê.</param>
/// <param name="OrderCode">Mã đơn thuê hiển thị cho người dùng.</param>
/// <param name="RentalItemId">Mã định danh duy nhất của mục thuê.</param>
/// <param name="EquipmentListingId">Mã định danh bài niêm yết thiết bị.</param>
/// <param name="ListingTitle">Tiêu đề bài niêm yết thiết bị.</param>
/// <param name="Quantity">Số lượng thiết bị thuê.</param>
/// <param name="StartDate">Ngày bắt đầu thuê.</param>
/// <param name="EndDate">Ngày kết thúc thuê.</param>
/// <param name="Status">Trạng thái hiện tại của mục thuê.</param>
public sealed record RenterCalendarEntryResponse(
    Guid OrderId,
    string OrderCode,
    Guid RentalItemId,
    Guid EquipmentListingId,
    string ListingTitle,
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status
)
{
    public string ListingTitleSnapshot => ListingTitle;
}

/// <summary>
/// DTO phản hồi lịch thuê cá nhân của người thuê trong một tháng.
/// </summary>
/// <param name="Month">Tháng truy vấn theo định dạng YYYY-MM.</param>
/// <param name="Days">Danh sách các ngày trong tháng kèm mật độ số lượng thiết bị thuê.</param>
/// <param name="Entries">Danh sách chi tiết các mục thuê trong tháng.</param>
/// <param name="Page">Số thứ tự trang hiện tại.</param>
/// <param name="PageSize">Kích thước trang.</param>
/// <param name="TotalCount">Tổng số mục thuê trong tháng.</param>
/// <param name="TotalPages">Tổng số trang kết quả.</param>
public sealed record RenterCalendarResponse(
    string Month,
    IReadOnlyList<RenterCalendarDayResponse> Days,
    IReadOnlyList<RenterCalendarEntryResponse> Entries,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);
