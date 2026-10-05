using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Hợp đồng service quản lý lịch tổng hợp và các khoảng chặn lịch (CalendarBlock) của tài sản.
/// </summary>
public interface ICalendarService
{
    Task<Result<CalendarResponse>> GetCalendarAsync(
        Guid assetId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<CalendarBlockResponse>> CreateCalendarBlockAsync(
        Guid assetId,
        CreateCalendarBlockRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteCalendarBlockAsync(
        Guid assetId,
        Guid blockId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
}
