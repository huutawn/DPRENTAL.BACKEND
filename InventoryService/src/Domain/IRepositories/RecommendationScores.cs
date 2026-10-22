namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Điểm số sở thích danh mục thiết bị của người dùng cá nhân (Personal Interest).
/// </summary>
public sealed record EquipmentTypeInterestScore(Guid EquipmentTypeId, long Score);

/// <summary>
/// Điểm số mức độ phổ biến toàn cục của listing thiết bị (Global Popularity).
/// </summary>
public sealed record ListingPopularityScore(Guid ListingId, long Score);
