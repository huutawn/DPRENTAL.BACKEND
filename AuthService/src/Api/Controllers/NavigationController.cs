using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Cung cấp navigation Identity đã lọc theo quyền database của user và organization context tùy chọn.</summary>
[ApiController]
[Route("navigation")]
[Produces("application/json")]
[Authorize]
public sealed class NavigationController(INavigationService navigationService) : ControllerBase
{
    /// <summary>
    /// Lấy navigation của một dashboard organization. OrganizationId là bắt buộc; endpoint chỉ yêu cầu authenticated user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<NavigationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NavigationResponse>> Get(
        [FromQuery] Guid? organizationId,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result<NavigationResponse> result = await navigationService.GetNavigationAsync(userId, organizationId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(result.Value);
    }
}
