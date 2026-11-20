using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Returns;

public interface IReturnService
{
    Task<Result<ReturnRequestResponse>> CreateReturnRequestAsync(Guid itemId, IRentalCallerContext caller,
        CreateReturnRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Result<RecordReturnResponse>> RecordReturnAsync(Guid itemId, IRentalCallerContext caller,
        RecordReturnRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Result<ReturnRequestResponse>> ReportReturnIssueAsync(Guid itemId, Guid returnRequestId,
        IRentalCallerContext caller, ReportReturnIssueRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<AdminReturnRequestSummaryResponse>>> GetAdminReturnRequestsAsync(
        AdminReturnRequestQuery query, IRentalCallerContext caller, CancellationToken cancellationToken = default);

    Task<Result<AdminReturnRequestDetailResponse>> GetAdminReturnRequestDetailAsync(
        Guid returnRequestId, IRentalCallerContext caller, CancellationToken cancellationToken = default);

    Task<Result<ReturnRequestResponse>> ResolveReturnRequestAsync(
        Guid itemId, Guid returnRequestId, IRentalCallerContext caller,
        AdminResolveReturnRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Result<MediaAccessDto>> GetReturnUnitMediaAccessAsync(
        Guid itemId, Guid returnRequestId, Guid assignmentId, Guid mediaId,
        IRentalCallerContext caller, CancellationToken cancellationToken = default);
}
