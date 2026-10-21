using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("items/{itemId:guid}/returns")]
public sealed class RentalReturnsController(IReturnService returns, IRentalCallerContext caller) : ControllerBase
{
    /// <summary>Supplier ghi nhận thu hồi một hoặc nhiều máy đã bàn giao.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RecordReturnResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecordReturn(Guid itemId, [FromBody] RecordReturnRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await returns.RecordReturnAsync(itemId, caller, request, idempotencyKey ?? string.Empty, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
