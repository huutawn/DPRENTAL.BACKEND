using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.SupplierStatistics;

/// <summary>
/// Service truy vấn thống kê xác nhận của supplier dành cho Admin.
/// </summary>
public interface ISupplierStatisticsService
{
    Task<Result<SupplierConfirmationStatisticsResponse>> GetConfirmationStatisticsAsync(
        SupplierConfirmationStatisticsRequest request,
        IRentalCallerContext callerContext,
        CancellationToken cancellationToken = default);
}
