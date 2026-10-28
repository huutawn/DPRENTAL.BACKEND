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
}
