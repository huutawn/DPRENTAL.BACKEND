using Dprental.RentalService.Application.DTOs.Calendar;
using Dprental.RentalService.Application.IServices;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("me")]
public sealed class RenterCalendarController : ControllerBase
{
    private readonly IRentalCalendarService _calendarService;
    private readonly IRentalCallerContext _callerContext;

    public RenterCalendarController(
        IRentalCalendarService calendarService,
        IRentalCallerContext callerContext)
    {
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Đọc lịch thuê cá nhân của người thuê hiện tại (/api/v1/rental/me/calendar).
    /// </summary>
    /// <param name="month">Tháng truy vấn theo định dạng YYYY-MM (mặc định tháng hiện tại theo giờ Việt Nam).</param>
    /// <param name="page">Số trang phân trang.</param>
    /// <param name="pageSize">Kích thước trang (tối đa 100).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Dữ liệu lịch thuê cá nhân gồm các ngày trong tháng và danh sách entries.</returns>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(RenterCalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMyCalendar(
        [FromQuery] string? month,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _calendarService.GetRenterCalendarAsync(month, page, pageSize, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            var problem = new ProblemDetails
            {
                Status = result.Error.Type switch
                {
                    ErrorType.NotFound => StatusCodes.Status404NotFound,
                    ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
                    ErrorType.Forbidden => StatusCodes.Status403Forbidden,
                    ErrorType.Conflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest
                },
                Title = result.Error.Code,
                Detail = result.Error.Message
            };
            return StatusCode(problem.Status.Value, problem);
        }

        return Ok(result.Value);
    }
}
