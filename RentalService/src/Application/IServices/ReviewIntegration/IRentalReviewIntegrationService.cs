using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.ReviewIntegration;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.ReviewIntegration;

/// <summary>
/// Hợp đồng dịch vụ Application cung cấp snapshot và đối soát phục vụ tích hợp ReviewService.
/// </summary>
public interface IRentalReviewIntegrationService
{
    /// <summary>
    /// Lấy snapshot authoritative của mục thuê theo đơn hàng.
    /// </summary>
    Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đối soát mục thuê đã Completed trong cửa sổ 30 ngày theo keyset pagination.
    /// </summary>
    Task<Result<RentalReviewReconciliationBatchDto>> GetReconciliationBatchAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
