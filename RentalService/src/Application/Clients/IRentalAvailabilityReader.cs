using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Kết quả kiểm tra tồn kho thiết bị từ Inventory.
/// </summary>
/// <param name="AvailableQuantity">Số lượng sản phẩm khả dụng trong khoảng thời gian yêu cầu.</param>
/// <param name="IsAvailable">Xác nhận có đủ số lượng yêu cầu hay không.</param>
public sealed record RentalAvailabilityResult(int AvailableQuantity, bool IsAvailable);

public interface IRentalAvailabilityReader
{
    Task<Result<RentalAvailabilityResult>> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        int requestedQuantity,
        CancellationToken cancellationToken = default);
}
