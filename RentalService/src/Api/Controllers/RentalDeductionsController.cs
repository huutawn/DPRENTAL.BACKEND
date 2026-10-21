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
public sealed class RentalDeductionsController(IDeductionService deductions,
    IDeductionResponseService responses, IRentalCallerContext caller) : ControllerBase
{
    /// <summary>Nhà cung cấp gửi đề nghị khấu trừ tiền cọc sau khi đã thu hồi đủ các máy.</summary>
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

    /// <summary>Người thuê chấp nhận hoặc khiếu nại đề nghị khấu trừ tiền cọc.</summary>
    [HttpPost("response")]
    [ProducesResponseType(typeof(RespondDeductionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Respond(Guid itemId, [FromBody] RespondDeductionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await responses.RespondAsync(itemId, caller, request,
            idempotencyKey ?? string.Empty, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Quản trị viên xem đề nghị khấu trừ, khiếu nại và bằng chứng thu hồi của từng máy.</summary>
    [HttpGet("dispute")]
    [ProducesResponseType(typeof(DisputeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDispute(Guid itemId, CancellationToken cancellationToken)
    {
        var result = await responses.GetDisputeDetailAsync(itemId, caller, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Quản trị viên chốt số tiền khấu trừ sau khi người thuê khiếu nại.</summary>
    [HttpPost("dispute/resolution")]
    [ProducesResponseType(typeof(ResolveDisputeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Resolve(Guid itemId, [FromBody] ResolveDisputeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await responses.ResolveDisputeAsync(itemId, caller, request,
            idempotencyKey ?? string.Empty, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
