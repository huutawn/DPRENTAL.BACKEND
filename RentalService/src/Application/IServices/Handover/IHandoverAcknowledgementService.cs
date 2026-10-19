using Dprental.RentalService.Application.DTOs.Handover;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Handover;

public interface IHandoverAcknowledgementService
{
    Task<Result<AcknowledgeReceiptResponse>> AcknowledgeAsync(
        Guid itemId,
        AcknowledgeReceiptRequest? request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
