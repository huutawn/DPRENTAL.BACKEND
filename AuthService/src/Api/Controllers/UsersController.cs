using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Cung cấp hồ sơ, quyền và organization của người dùng đang đăng nhập.</summary>
[ApiController]
[Route("users")]
[Produces("application/json")]
[Authorize]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    /// <summary>Lấy hồ sơ, role, permission và organization đang hoạt động của người dùng hiện tại.</summary>
    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result<CurrentUserResponse> result = await userService.GetCurrentUserProfileAsync(userId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
