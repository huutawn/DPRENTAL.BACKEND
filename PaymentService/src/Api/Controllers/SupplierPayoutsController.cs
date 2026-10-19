using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/supplier/payouts")]
[Authorize(Policy = "FinancialReadClientPolicy")]
public sealed class SupplierPayoutsController(IFinancialReadService financialRead) : ControllerBase
{
    /// <summary>Supplier chỉ xem các khoản payout thuộc mình.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(FinancialPage<SupplierPayoutListItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] FinancialListQuery query, CancellationToken ct)
    {
        if (!CallerIdentity.TryGetUserId(User, out var supplierId)) return Forbid();
        var result = await financialRead.ListSupplierPayoutsAsync(supplierId, query, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
