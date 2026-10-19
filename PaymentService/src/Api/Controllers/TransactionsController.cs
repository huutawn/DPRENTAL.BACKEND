using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/transactions")]
[Authorize(Policy = "FinancialReadAdminPolicy")]
public sealed class TransactionsController(IFinancialReadService financialRead) : ControllerBase
{
    /// <summary>Admin xem giao dịch để đối soát, không trả dữ liệu tài khoản hoặc webhook thô.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TransactionDetailResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await financialRead.GetTransactionAsync(id, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
