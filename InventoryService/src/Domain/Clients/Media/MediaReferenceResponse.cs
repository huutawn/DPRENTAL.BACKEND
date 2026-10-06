using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Domain.Clients.Media;

/// <summary>
/// Đại diện cho tham chiếu media trả về phía client gồm ID và URL truy cập công khai ổn định.
/// </summary>
public sealed record MediaReferenceResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("url")] string Url
);
