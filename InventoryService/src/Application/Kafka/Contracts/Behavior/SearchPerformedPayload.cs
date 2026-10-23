using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Kafka.Contracts.Behavior;

/// <summary>
/// Payload sự kiện SearchPerformed phát ra khi tìm kiếm trang 1 thành công có từ khóa hoặc bộ lọc.
/// </summary>
public sealed record SearchPerformedPayload
{
    [JsonPropertyName("userId")]
    public Guid? UserId { get; init; }

    [JsonPropertyName("equipmentTypeId")]
    public Guid? EquipmentTypeId { get; init; }

    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("categoryId")]
    public Guid? CategoryId { get; init; }

    [JsonPropertyName("filterContext")]
    public SearchFilterContext? FilterContext { get; init; }

    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }
}

public sealed record SearchFilterContext
{
    [JsonPropertyName("brandId")]
    public Guid? BrandId { get; init; }

    [JsonPropertyName("provinceCode")]
    public string? ProvinceCode { get; init; }

    [JsonPropertyName("wardCode")]
    public string? WardCode { get; init; }

    [JsonPropertyName("sort")]
    public string? Sort { get; init; }

    [JsonPropertyName("filters")]
    public string? Filters { get; init; }

    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }

    [JsonPropertyName("requestedQuantity")]
    public int? RequestedQuantity { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}
