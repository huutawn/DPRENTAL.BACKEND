using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.DTOs.Calendar;

/// <summary>
/// Thống kê chi tiết một ngày trong lịch thuê của người thuê.
/// </summary>
/// <param name="Date">Ngày trong tháng.</param>
/// <param name="RentedQuantity">Tổng số lượng thiết bị đang thuê trong ngày.</param>
public sealed record RenterCalendarDayResponse(
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("rentedQuantity")] int RentedQuantity
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
    [property: JsonPropertyName("orderId")] Guid OrderId,
    [property: JsonPropertyName("orderCode")] string OrderCode,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("listingTitle")] string ListingTitle,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("status")] string Status
)
{
    [JsonPropertyName("listingTitleSnapshot")]
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
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("days")] IReadOnlyList<RenterCalendarDayResponse> Days,
    [property: JsonPropertyName("entries")] IReadOnlyList<RenterCalendarEntryResponse> Entries,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("totalPages")] int TotalPages
);
