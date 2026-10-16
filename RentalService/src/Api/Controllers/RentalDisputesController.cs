using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("admin/disputes")]
public sealed class RentalDisputesController(IDeductionResponseService responses,
    IRentalCallerContext caller) : ControllerBase
{
    /// <summary>Quản trị viên xem danh sách khiếu nại chưa được giải quyết.</summary>
    /// <remarks>Trả về danh sách phân trang các khiếu nại đang chờ quản trị viên quyết định.</remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetOpen([FromQuery] int skip = 0, [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await responses.GetOpenDisputesAsync(caller, skip, take, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
