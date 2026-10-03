using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API quản lý lịch tổng hợp và các khoảng chặn lịch (CalendarBlock) của tài sản theo DPR-149.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/inventory/assets/{assetId:guid}")]
public sealed class InventoryCalendarController : ControllerBase
{
    private readonly ICalendarService _calendarService;
    private readonly IInventoryCallerContext _callerContext;

    public InventoryCalendarController(
        ICalendarService calendarService,
        IInventoryCallerContext callerContext)
    {
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Đọc lịch tổng hợp của tài sản trong cửa sổ MVP (từ hôm nay đến 6 tháng sau).
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Dữ liệu lịch tổng hợp gồm các khoảng CalendarBlock, Held và Allocated.</returns>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(CalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCalendar(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var result = await _calendarService.GetCalendarAsync(assetId, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Tạo khoảng chặn lịch mới trên tài sản.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="request">Thông tin khoảng chặn lịch.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Thông tin khoảng chặn lịch đã tạo.</returns>
    [HttpPost("calendar-blocks")]
    [ProducesResponseType(typeof(CalendarBlockResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCalendarBlock(
        Guid assetId,
        [FromBody] CreateCalendarBlockRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _calendarService.CreateCalendarBlockAsync(assetId, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Xóa khoảng chặn lịch trên tài sản.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="blockId">Định danh khoảng chặn lịch cần xóa.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>204 No Content nếu xóa thành công.</returns>
    [HttpDelete("calendar-blocks/{blockId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCalendarBlock(
        Guid assetId,
        Guid blockId,
        CancellationToken cancellationToken)
    {
        var result = await _calendarService.DeleteCalendarBlockAsync(assetId, blockId, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return NoContent();
    }
}
