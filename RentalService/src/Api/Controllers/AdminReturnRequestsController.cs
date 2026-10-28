using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("admin/return-requests")]
public sealed class AdminReturnRequestsController(
    IReturnService returns,
    IRentalCallerContext caller) : ControllerBase
{
    /// <summary>Danh sách các yêu cầu trả máy cho Admin tra cứu, lọc disputed / overdue và phân trang.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<AdminReturnRequestSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetReturnRequests(
        [FromQuery] AdminReturnRequestQuery query,
        CancellationToken cancellationToken)
    {
        var result = await returns.GetAdminReturnRequestsAsync(query, caller, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Chi tiết một yêu cầu trả máy, gồm request, từng unit, reports, quyết định trước đó và trạng thái Inventory liên quan.</summary>
    [HttpGet("{returnRequestId:guid}")]
    [ProducesResponseType(typeof(AdminReturnRequestDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetReturnRequestDetail(
        Guid returnRequestId,
        CancellationToken cancellationToken)
    {
        var result = await returns.GetAdminReturnRequestDetailAsync(returnRequestId, caller, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
