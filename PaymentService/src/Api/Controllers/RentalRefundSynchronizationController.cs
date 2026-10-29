using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(Policy = "RentalRefundSyncPolicy")]
[Route("internal/rental-refunds/synchronize")]
public sealed class RentalRefundSynchronizationController(IManualRefundService refunds) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Synchronize(RentalRefundSyncRequest request, CancellationToken ct)
    {
        var result = await refunds.SynchronizeRentalAsync(request, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
