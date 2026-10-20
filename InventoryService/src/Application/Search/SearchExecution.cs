namespace Dprental.InventoryService.Application.Search;

/// <summary>
/// Kết quả thực thi tìm kiếm kèm định danh backend phục vụ trả header X-Search-Backend ở Controller.
/// </summary>
/// <typeparam name="T">Kiểu dữ liệu kết quả phân trang.</typeparam>
public sealed record SearchExecution<T>(T Value, string SearchBackend);
