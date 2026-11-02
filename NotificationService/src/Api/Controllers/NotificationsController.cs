using Dprental.NotificationService.Api.Filters;
using Dprental.NotificationService.Api.Security;
using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.NotificationService.Api.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationQueryService _queryService;
    private readonly INotificationReadService _readService;

    public NotificationsController(
        INotificationQueryService queryService,
        INotificationReadService readService)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _readService = readService ?? throw new ArgumentNullException(nameof(readService));
    }

    /// <summary>
    /// Lấy danh sách thông báo của người dùng hiện tại có phân trang và bộ lọc.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = "all",
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var userResult = User.TryGetHumanUserId();
        if (userResult.IsFailure)
        {
            return this.ToProblem(userResult.Error);
        }

        var result = await _queryService.GetNotificationsAsync(
            userResult.Value,
            category,
            status,
            page,
            pageSize,
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Lấy số lượng thông báo chưa đọc của người dùng hiện tại.
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetUnreadCount(CancellationToken cancellationToken = default)
    {
        var userResult = User.TryGetHumanUserId();
        if (userResult.IsFailure)
        {
            return this.ToProblem(userResult.Error);
        }

        var result = await _queryService.GetUnreadCountAsync(userResult.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đánh dấu đã đọc một thông báo cụ thể.
    /// </summary>
    [HttpPatch("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken cancellationToken = default)
    {
        var userResult = User.TryGetHumanUserId();
        if (userResult.IsFailure)
        {
            return this.ToProblem(userResult.Error);
        }

        var result = await _readService.MarkAsReadAsync(id, userResult.Value, cancellationToken);
        return result.IsSuccess ? Ok() : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Đánh dấu đã đọc toàn bộ thông báo của người dùng với server cutoff timestamp.
    /// </summary>
    [HttpPatch("read-all")]
    [ProducesResponseType(typeof(MarkAllReadResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken = default)
    {
        var userResult = User.TryGetHumanUserId();
        if (userResult.IsFailure)
        {
            return this.ToProblem(userResult.Error);
        }

        var result = await _readService.MarkAllAsReadAsync(userResult.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
