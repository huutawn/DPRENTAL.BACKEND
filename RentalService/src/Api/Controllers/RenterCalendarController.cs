using Dprental.RentalService.Application.DTOs.Calendar;
using Dprental.RentalService.Application.IServices.Calendar;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Quản lý lịch thuê cá nhân (Renter Calendar) của người thuê thiết bị.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
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
    /// Tra cứu lịch thuê thiết bị cá nhân của người thuê theo từng tháng.
    /// </summary>
    /// <param name="month">Tháng truy vấn theo định dạng <c>YYYY-MM</c> (ví dụ: <c>2026-10</c>). Mặc định là tháng hiện tại theo giờ Việt Nam.</param>
    /// <param name="page">Số thứ tự trang cần lấy trong danh sách entries (mặc định là 1).</param>
    /// <param name="pageSize">Số lượng mục thuê tối đa trên mỗi trang (mặc định là 50, giới hạn tối đa 100).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy dữ liệu lịch thuê thành công.</response>
    /// <response code="400">Tháng không hợp lệ (INVALID_MONTH_FORMAT) hoặc kiểu dữ liệu phân trang không hợp lệ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền truy cập dữ liệu này.</response>
    /// <returns>Dữ liệu lịch thuê cá nhân gồm tổng hợp ngày trong tháng và danh sách mục thuê chi tiết.</returns>
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
