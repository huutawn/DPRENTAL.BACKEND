using Dprental.NotificationService.Api.Filters;
using Dprental.NotificationService.Api.Security;
using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.NotificationService.Api.Controllers;

[ApiController]
[Route("api/v1/notifications/preferences")]
[Authorize]
public sealed class PreferencesController : ControllerBase
{
    private readonly IPreferenceService _preferenceService;

    public PreferencesController(IPreferenceService preferenceService)
    {
        _preferenceService = preferenceService ?? throw new ArgumentNullException(nameof(preferenceService));
    }

    /// <summary>
    /// Lấy danh sách tùy chọn nhận email thông báo của người dùng hiện tại theo các danh mục.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PreferencesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken = default)
    {
        var userResult = User.TryGetHumanUserId();
        if (userResult.IsFailure)
        {
            return this.ToProblem(userResult.Error);
        }

        var result = await _preferenceService.GetPreferencesAsync(userResult.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Cập nhật tùy chọn nhận email thông báo cho người dùng hiện tại.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(PreferencesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdatePreferences(
        [FromBody] UpdatePreferencesRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var userResult = User.TryGetHumanUserId();
        if (userResult.IsFailure)
        {
            return this.ToProblem(userResult.Error);
        }

        var result = await _preferenceService.UpdatePreferencesAsync(userResult.Value, request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
