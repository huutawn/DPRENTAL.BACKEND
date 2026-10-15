using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Giao diện thực thi giao dịch ghi nguyên tử có khóa phân cấp (Group -> Listing -> Asset).
/// </summary>
public interface IInventoryWriteTransaction
{
    /// <summary>
    /// Thực thi thao tác ghi nguyên tử trên mức tài sản đơn lẻ.
    /// Thứ tự khóa: Listing cha (nếu tồn tại) -> AssetId.
    /// </summary>
    Task<Result<T>> ExecuteAsync<T>(
        Guid assetId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thực thi thao tác ghi nguyên tử trên mức EquipmentListing.
    /// Khóa ListingId bằng advisory lock trước khi thực thi.
    /// </summary>
    Task<Result<T>> ExecuteListingWriteAsync<T>(
        Guid listingId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thực thi thao tác giữ chỗ theo nhóm (Group Hold).
    /// Thứ tự khóa thống nhất: (sourceSystem, groupReference) -> ListingId -> Asset IDs theo thứ tự tăng dần.
    /// </summary>
    Task<Result<T>> ExecuteGroupHoldAsync<T>(
        string sourceSystem,
        string groupReference,
        Guid listingId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thực thi lệnh phân bổ (Allocation Command) với khóa tuần tự:
    /// (sourceSystem, operationKey) -> (sourceSystem, groupReference) -> ListingId (nếu có) -> Asset IDs tăng dần.
    /// </summary>
    Task<Result<T>> ExecuteAllocationCommandAsync<T>(
        string sourceSystem,
        string operationKey,
        string groupReference,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default);
}
