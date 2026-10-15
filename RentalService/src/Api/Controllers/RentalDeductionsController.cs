using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("items/{itemId:guid}/deductions")]
public sealed class RentalDeductionsController(IDeductionService deductions, IRentalCallerContext caller) : ControllerBase
{
    /// <summary>Supplier submits one deposit deduction proposal after all assigned assets are returned.</summary>
    /// <remarks>Requires Idempotency-Key. The total deduction is for the RentalItem; affected asset IDs identify damaged units and do not receive invented per-unit amounts.</remarks>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitDeductionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Submit(Guid itemId, [FromBody] SubmitDeductionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await deductions.SubmitAsync(itemId, caller, request, idempotencyKey ?? string.Empty, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
