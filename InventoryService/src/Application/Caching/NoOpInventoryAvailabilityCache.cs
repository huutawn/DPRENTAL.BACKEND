using Dprental.InventoryService.Application.DTOs.Availability;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Cache no-op mặc định khi cache bị vô hiệu hóa hoặc chưa được cấu hình.
/// </summary>
public sealed class NoOpInventoryAvailabilityCache : IInventoryAvailabilityCache
{
    public static readonly NoOpInventoryAvailabilityCache Instance = new();

    public bool IsEnabled => false;

    public Task<IReadOnlyDictionary<string, CachedMonthRanges?>> GetMonthRangesBulkAsync(
        Guid assetId,
        IReadOnlyList<string> monthKeys,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, CachedMonthRanges?> result = monthKeys.ToDictionary(k => k, _ => (CachedMonthRanges?)null);
        return Task.FromResult(result);
    }

    public Task SetMonthRangesAsync(
        Guid assetId,
        string monthKey,
        IReadOnlyList<BusyRangeDto> ranges,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task InvalidateAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
