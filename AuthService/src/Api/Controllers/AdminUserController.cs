using AuthService.Domain.DTOs.User;
using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Pagination;
using Dprental.AuthService.Domain.DTOs.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Api.Controllers;

[ApiController]
[Route("admin/users")]
[Produces("application/json")]
[Authorize] // Tạm thời dùng Authorize chung, bạn sẽ bổ sung Policy [Authorize(Policy = "...")] sau khi hoàn thiện Catalog quyền mới
public sealed class AdminUsersController(IUserService userService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.Users.Read)]
    [ProducesResponseType<PagedResult<AdminUserListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AdminUserListItemDto>>> GetUsersPaged(
         [FromQuery] GetUsersRequest request, 
         CancellationToken cancellationToken = default)
    {
        var pageRequest = new PageRequest(request.Page, request.PageSize);

        var result = await userService.GetUsersPagedAsync(pageRequest, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.Users.Read)]
    [ProducesResponseType<AdminUserDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserDetailDto>> GetUserById(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.GetUserByIdAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = Permissions.Users.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuspendUser(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.Identity?.Name, out Guid adminId)) return Unauthorized();

        var result = await userService.SuspendUserAsync(id, adminId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Permissions.Users.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateUser(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.Identity?.Name, out Guid adminId)) return Unauthorized();

        var result = await userService.ActivateUserAsync(id, adminId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }
}