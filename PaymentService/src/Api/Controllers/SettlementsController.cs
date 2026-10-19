using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/settlements")]
public sealed class SettlementsController : ControllerBase
{
    private readonly ISettlementService _settlements;
    private readonly IFinancialReadService _financialRead;

    public SettlementsController(ISettlementService settlements, IFinancialReadService financialRead)
    {
        _settlements = settlements;
        _financialRead = financialRead;
    }

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

    /// <summary>Đọc quyết toán của đơn thuê mà renter hoặc supplier sở hữu.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "FinancialReadClientPolicy")]
    [ProducesResponseType(typeof(SettlementDetailResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!CallerIdentity.TryGetCaller(User, out var caller)) return Forbid();
        var result = await _financialRead.GetSettlementAsync(id, caller, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
