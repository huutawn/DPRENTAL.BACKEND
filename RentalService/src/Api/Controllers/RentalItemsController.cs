using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.SupplierDecision;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("items")]
public sealed class RentalItemsController : ControllerBase
{
    private readonly ISupplierDecisionService _decisionService;
    private readonly IRentalCallerContext _callerContext;

    public RentalItemsController(
        ISupplierDecisionService decisionService,
        IRentalCallerContext callerContext)
    {
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Supplier chấp nhận yêu cầu thuê cho một mục thuê (POST /api/v1/rental/items/{itemId}/accept).
    /// </summary>
    [HttpPost("{itemId:guid}/accept")]
    [ProducesResponseType(typeof(SupplierDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AcceptItem(
        [FromRoute] Guid itemId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        var result = await _decisionService.AcceptItemAsync(itemId, idempotencyKey, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Supplier từ chối yêu cầu thuê cho một mục thuê (POST /api/v1/rental/items/{itemId}/reject).
    /// </summary>
    [HttpPost("{itemId:guid}/reject")]
    [ProducesResponseType(typeof(SupplierDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RejectItem(
        [FromRoute] Guid itemId,
        [FromBody] SupplierRejectRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        var result = await _decisionService.RejectItemAsync(itemId, request, idempotencyKey, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
