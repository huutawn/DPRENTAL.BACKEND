using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Giao diện dịch vụ ứng dụng quản lý vòng đời giữ chỗ và phân bổ tài sản (Hold &amp; Allocation Core).
/// </summary>
public interface IInventoryAllocationService
{
    /// <summary>
    /// Tạo mới một lượt giữ chỗ tạm thời (Hold) hoặc trả về lượt hiện tại nếu idempotent replay.
    /// </summary>
    Task<Result<CreateHoldResult>> CreateHoldAsync(
        Guid assetId,
        CreateHoldRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chuyển đổi lượt giữ chỗ còn hạn sang trạng thái phân bổ chính thức (Held -&gt; Allocated).
    /// </summary>
    Task<Result<AllocationResponse>> AllocateAsync(
        Guid allocationId,
        AllocateRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
}
