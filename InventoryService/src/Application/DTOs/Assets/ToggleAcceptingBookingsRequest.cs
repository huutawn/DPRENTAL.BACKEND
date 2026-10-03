using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Serialization.Converters;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Yêu cầu bật hoặc tắt nhận đặt lịch trên tài sản.
/// </summary>
[JsonConverter(typeof(ToggleAcceptingBookingsRequestJsonConverter))]
public sealed record ToggleAcceptingBookingsRequest(
    [property: JsonPropertyName("acceptingBookings")] bool AcceptingBookings,
    [property: JsonPropertyName("reason")] string? Reason = null
);
