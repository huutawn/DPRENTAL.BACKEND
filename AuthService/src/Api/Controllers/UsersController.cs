using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Thông tin của chính người dùng đang đăng nhập.</summary>
[ApiController]
[Route("api/v1/users")]
[Produces("application/json")]
[Authorize]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrentUser(CancellationToken cancellationToken)
    {
        // NameClaimType được cấu hình là claim "sub" (xem JwtBearerServiceCollectionExtensions).
        if (!Guid.TryParse(User.Identity?.Name, out Guid userId))
        {
            return Unauthorized();
        }

        Result<CurrentUserResponse> result = await userService.GetCurrentUserProfileAsync(userId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
