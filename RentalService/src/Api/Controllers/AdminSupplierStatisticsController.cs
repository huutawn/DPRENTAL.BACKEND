using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.SupplierStatistics;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Endpoint quản trị thống kê xác nhận của supplier từ Rental history.
/// </summary>
[ApiController]
[Authorize]
[Route("admin/suppliers")]
public sealed class AdminSupplierStatisticsController : ControllerBase
{
    private readonly ISupplierStatisticsService _statisticsService;
    private readonly IRentalCallerContext _callerContext;

    public AdminSupplierStatisticsController(
        ISupplierStatisticsService statisticsService,
        IRentalCallerContext callerContext)
    {
        _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Thống kê xác nhận của supplier theo khoảng thời gian (GET /api/v1/rental/admin/suppliers/{supplierId}/confirmation-statistics).
    /// </summary>
    [HttpGet("{supplierId}/confirmation-statistics")]
    [ProducesResponseType(typeof(SupplierConfirmationStatisticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetConfirmationStatistics(
        [FromRoute] string supplierId,
        [FromQuery] string? fromUtc,
        [FromQuery] string? toUtc,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(supplierId, out var parsedSupplierId) || parsedSupplierId == Guid.Empty)
        {
            return this.ToProblem(Error.Validation("INVALID_SUPPLIER_ID", "Supplier ID không hợp lệ."));
        }

        var request = new SupplierConfirmationStatisticsRequest(parsedSupplierId, fromUtc, toUtc);
        var result = await _statisticsService.GetConfirmationStatisticsAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
