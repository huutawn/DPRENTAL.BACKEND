namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Cấu hình Valkey read cache cho Inventory Service theo DPR-150.
/// Thuộc Application layer để các use cases có thể sử dụng cấu hình tập trung.
/// </summary>
public sealed class InventoryCacheOptions
{
    public const string SectionName = "InventoryCache";

    /// <summary>
    /// Bật hoặc tắt Valkey cache. Mặc định tắt (false).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Thời gian sống mặc định của cache tính theo giây. Mặc định 60 giây.
    /// </summary>
    public int TtlSeconds { get; set; } = 60;

    /// <summary>
    /// Thời gian chờ thao tác cache tính theo mili-giây. Mặc định 500 ms.
    /// </summary>
    public int TimeoutMs { get; set; } = 500;
}
