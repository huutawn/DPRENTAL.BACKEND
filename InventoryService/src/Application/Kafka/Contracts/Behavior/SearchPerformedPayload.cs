
namespace Dprental.InventoryService.Application.Kafka.Contracts.Behavior;

/// <summary>
/// Payload sự kiện SearchPerformed phát ra khi tìm kiếm trang 1 thành công có từ khóa hoặc bộ lọc.
/// </summary>
public sealed record SearchPerformedPayload
{
    public Guid? UserId { get; init; }

    public Guid? EquipmentTypeId { get; init; }

    public string? Query { get; init; }

    public Guid? CategoryId { get; init; }

    public SearchFilterContext? FilterContext { get; init; }

    public int ResultCount { get; init; }
}

public sealed record SearchFilterContext
{
    public Guid? BrandId { get; init; }

    public string? ProvinceCode { get; init; }

    public string? WardCode { get; init; }

    public string? Sort { get; init; }

    public string? Filters { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public int? RequestedQuantity { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; }
}
