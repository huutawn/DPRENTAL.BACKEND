using AuthService.Domain.DTOs.User;
using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Endpoint quản trị dành cho admin để tra cứu và quản lý trạng thái người dùng.</summary>
[ApiController]
[Route("admin/users")]
[Produces("application/json")]
[Authorize] // Tạm thời dùng Authorize chung, bạn sẽ bổ sung Policy [Authorize(Policy = "...")] sau khi hoàn thiện Catalog quyền mới
public sealed class AdminUsersController(IUserService userService) : ControllerBase
{
    /// <summary>Lấy danh sách người dùng có phân trang.</summary>
    /// <param name="request">Tham số phân trang và tìm kiếm (page, pageSize, search).</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpGet]
    [Authorize(Policy = Permissions.Users.Read)]
    [ProducesResponseType<PagedResult<AdminUserListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AdminUserListItemDto>>> GetUsersPaged(
     [FromQuery] GetUsersRequest request,
     CancellationToken cancellationToken = default)
    {
        var pageRequest = new PageRequest(request.Page, request.PageSize, request.Search);

        var result = await userService.GetUsersPagedAsync(pageRequest, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy chi tiết một người dùng theo id.</summary>
    /// <param name="id">Id của người dùng.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.Users.Read)]
    [ProducesResponseType<AdminUserDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserDetailDto>> GetUserById(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.GetUserByIdAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Tạm khóa (suspend) một người dùng.</summary>
    /// <param name="id">Id của người dùng cần khóa.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
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

    /// <summary>Kích hoạt lại một người dùng đã bị khóa.</summary>
    /// <param name="id">Id của người dùng cần kích hoạt.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
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
