using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/manual-refunds")]
public sealed class ManualRefundsController(IManualRefundService refunds) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "ManualRefundAdminPolicy")]
    public async Task<IActionResult> List([FromQuery] int status = 1, [FromQuery] Guid? cursor = null,
        [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var result = await refunds.ListAsync(status, cursor, limit, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    [HttpGet("{requirementId:guid}")]
    [Authorize(Policy = "ManualRefundAdminPolicy")]
    public async Task<IActionResult> Get(Guid requirementId, CancellationToken cancellationToken)
    {
        var result = await refunds.GetAsync(requirementId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Xác nhận admin đã chuyển khoản hoàn cọc ngoài hệ thống.</summary>
    [HttpPost("{requirementId:guid}/complete")]
    [Authorize(Policy = "ManualRefundAdminPolicy")]
    [ProducesResponseType(typeof(CompleteManualRefundResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(
        Guid requirementId, [FromBody] CompleteManualRefundRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetSubjectUserId(out var adminId)) return Forbid();
        var result = await refunds.CompleteAsync(requirementId, request, adminId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
