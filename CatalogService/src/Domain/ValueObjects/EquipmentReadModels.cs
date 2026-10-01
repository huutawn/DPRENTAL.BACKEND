using Microsoft.EntityFrameworkCore.Storage.ValueConversion.Internal;

namespace Dprental.CatalogService.Domain.ValueObjects;

public sealed record CategorySummary(Guid Id, string Name);

public sealed record EquipmentListItem(
    Guid Id,
    string Name,
    string? Brand,
    decimal DailyPrice,
    Guid RegionId,
    Guid? ThumbnailMediaId,
    CategorySummary Category);

public sealed record EquipmentDetail(
    Guid Id,
    string Code,
    string Name,
    string? Brand,
    string? Description,
    decimal DailyPrice,
    Guid RegionId,
    CategorySummary Category,
    string? SpecificationsJson,
    IReadOnlyList<Guid> MediaIds);

public sealed record PagedResult<T>(IReadOnlyCollection<T> Items, int TotalItems);