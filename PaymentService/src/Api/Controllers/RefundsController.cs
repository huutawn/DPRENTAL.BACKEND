using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/refunds")]
public sealed class RefundsController(IManualRefundService refunds) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "RefundServicePolicy")]
    public async Task<IActionResult> RequestRefund([FromBody] RequestRefundRequest request, CancellationToken cancellationToken)
    {
        var result = await refunds.RequestAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
