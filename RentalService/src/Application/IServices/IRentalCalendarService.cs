using Dprental.RentalService.Application.DTOs.Calendar;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices;

/// <summary>
/// Hợp đồng dịch vụ quản lý truy vấn lịch thuê cá nhân của renter.
/// </summary>
public interface IRentalCalendarService
{
    Task<Result<RenterCalendarResponse>> GetRenterCalendarAsync(
        string? month,
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
