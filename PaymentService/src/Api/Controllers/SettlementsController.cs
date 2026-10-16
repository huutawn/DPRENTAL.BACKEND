using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/settlements")]
public sealed class SettlementsController : ControllerBase
{
    private readonly ISettlementService _settlements;

    public SettlementsController(ISettlementService settlements) => _settlements = settlements;

    [HttpPost]
    [Authorize(Policy = "SettlementServicePolicy")]
    [ProducesResponseType(typeof(SettleRentalResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SettleRentalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Settle([FromBody] SettleRentalRequest request, CancellationToken ct)
    {
        var result = await _settlements.SettleAsync(request, ct);
        if (result.IsFailure) return this.ToProblem(result.Error);
        return result.Value.IsReplay
            ? Ok(result.Value)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }
}
