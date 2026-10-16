namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Cấu hình Valkey read cache cho toàn bộ Inventory Service.
/// </summary>
public sealed class InventoryCacheOptions
{
    public const string SectionName = "InventoryCache";

    /// <summary>
    /// Bật hoặc tắt Valkey cache. Mặc định tắt (false).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// TTL cho dữ liệu biến động cao (public discovery/detail, busy-ranges, availability). Mặc định 15 giây.
    /// </summary>
    public int DynamicTtlSeconds { get; set; } = 15;

    /// <summary>
    /// TTL cho dữ liệu metadata ít biến động (owner listings/assets/search, statistics, warehouse). Mặc định 120 giây.
    /// </summary>
    public int MetadataTtlSeconds { get; set; } = 120;

    /// <summary>
    /// TTL cho Catalog discovery cache (category -> type IDs, batch schemas). Mặc định 120 giây.
    /// </summary>
    public int CatalogTtlSeconds { get; set; } = 120;


    /// <summary>
    /// Thời gian chờ thao tác cache tính theo mili-giây. Mặc định 100 ms.
    /// </summary>
    public int TimeoutMs { get; set; } = 100;

    /// <summary>
    /// Giới hạn kích thước payload tối đa (byte). Mặc định 1 MiB (1,048,576 byte).
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Số lượng tối đa source fills chạy đồng thời trong cùng một process để chống thundering herd. Mặc định 32.
    /// </summary>
    public int MaxConcurrentFills { get; set; } = 32;

    /// <summary>
    /// Tên môi trường dùng cho namespace cache (mặc định rỗng, resolve tự động qua hosting environment).
    /// </summary>
    public string EnvironmentName { get; set; } = string.Empty;

    public void Validate()
    {
        if (DynamicTtlSeconds <= 0)
            throw new InvalidOperationException($"{SectionName}:DynamicTtlSeconds phải lớn hơn 0.");
        if (MetadataTtlSeconds <= 0)
            throw new InvalidOperationException($"{SectionName}:MetadataTtlSeconds phải lớn hơn 0.");
        if (CatalogTtlSeconds <= 0)
            throw new InvalidOperationException($"{SectionName}:CatalogTtlSeconds phải lớn hơn 0.");
        if (TimeoutMs <= 0)
            throw new InvalidOperationException($"{SectionName}:TimeoutMs phải lớn hơn 0.");
        if (MaxPayloadBytes <= 0)
            throw new InvalidOperationException($"{SectionName}:MaxPayloadBytes phải lớn hơn 0.");
        if (MaxConcurrentFills <= 0)
            throw new InvalidOperationException($"{SectionName}:MaxConcurrentFills phải lớn hơn 0.");
        if (!string.IsNullOrWhiteSpace(EnvironmentName) && (EnvironmentName.Contains('{') || EnvironmentName.Contains('}')))
            throw new InvalidOperationException($"{SectionName}:EnvironmentName không được chứa ký tự phá vỡ hash tag ('{{' hoặc '}}').");
    }
}
