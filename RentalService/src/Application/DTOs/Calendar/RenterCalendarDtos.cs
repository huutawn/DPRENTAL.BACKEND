using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.DTOs.Calendar;

/// <summary>
/// Thống kê chi tiết một ngày trong lịch thuê của renter.
/// </summary>
public sealed record RenterCalendarDayResponse(
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("rentedQuantity")] int RentedQuantity
);

/// <summary>
/// Chi tiết một mục thuê trong danh sách lịch thuê của renter.
/// Tuyệt đối không để lộ physical asset ID, serial number hoặc allocation reference của Supplier.
/// </summary>
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
/// DTO phản hồi lịch thuê cá nhân của renter trong một tháng.
/// </summary>
public sealed record RenterCalendarResponse(
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("days")] IReadOnlyList<RenterCalendarDayResponse> Days,
    [property: JsonPropertyName("entries")] IReadOnlyList<RenterCalendarEntryResponse> Entries,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("totalPages")] int TotalPages
);
