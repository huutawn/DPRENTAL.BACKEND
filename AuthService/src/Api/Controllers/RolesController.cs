using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Gán hoặc thu hồi role người dùng theo quyền RBAC của người gọi.</summary>
[ApiController]
[Route("roles")]
[Produces("application/json")]
[Authorize]
public sealed class RolesController(IRoleAssignmentService roleAssignmentService) : ControllerBase
{
    /// <summary>Gán role cho người dùng khi người gọi có quyền roles.assign.any.</summary>
    [HttpPost("assign")]
    [Authorize(Policy = Permissions.Roles.AssignAny)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(AssignRoleRequest request, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid actorUserId)
        {
            return Unauthorized();
        }

        Result result = await roleAssignmentService.AssignAsync(actorUserId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Thu hồi role của người dùng khi người gọi có quyền roles.revoke.</summary>
    [HttpPost("revoke")]
    [Authorize(Policy = Permissions.Roles.Revoke)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(AssignRoleRequest request, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid actorUserId)
        {
            return Unauthorized();
        }

        Result result = await roleAssignmentService.RevokeAsync(actorUserId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }
}
