using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Giao diện xử lý nghiệp vụ quản lý kho của bên cho thuê.
/// </summary>
public interface IWarehouseService
{
    /// <summary>
    /// Đọc thông tin kho của người dùng hiện tại (current user).
    /// </summary>
    Task<Result<WarehouseResponse>> GetMyWarehouseAsync(
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo mới hoặc thay thế toàn bộ địa chỉ kho của người dùng hiện tại.
    /// </summary>
    Task<Result<WarehouseResponse>> UpdateMyWarehouseAsync(
        UpdateWarehouseRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
}
