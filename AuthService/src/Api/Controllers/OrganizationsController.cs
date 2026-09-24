using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Quản lý organization và thành viên của organization mà người dùng được phép truy cập.</summary>
[ApiController]
[Route("organizations")]
[Produces("application/json")]
[Authorize]
public sealed class OrganizationsController(IOrganizationService organizationService) : ControllerBase
{
    /// <summary>Tạo organization mới và thêm người gọi làm thành viên ban đầu.</summary>
    [HttpPost]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateOrganizationRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationResponse> result = await organizationService.CreateAsync(userId, request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Lấy thông tin một organization khi người gọi có quyền truy cập.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationResponse> result = await organizationService.GetByIdAsync(userId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy danh sách organization có phân trang mà người gọi được phép xem.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<OrganizationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] PageRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<PagedResult<OrganizationResponse>> result = await organizationService.ListAsync(userId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Cập nhật thông tin organization khi người gọi có quyền quản lý.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, UpdateOrganizationRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result result = await organizationService.UpdateAsync(userId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Lấy danh sách thành viên của organization theo trang.</summary>
    [HttpGet("{id:guid}/members")]
    [ProducesResponseType<PagedResult<OrganizationMemberResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers(Guid id, [FromQuery] PageRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<PagedResult<OrganizationMemberResponse>> result = await organizationService.GetMembersAsync(userId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Thêm hoặc mời người dùng trở thành thành viên của organization.</summary>
    [HttpPost("{id:guid}/members")]
    [ProducesResponseType<OrganizationMemberResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddMember(Guid id, AddOrganizationMemberRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationMemberResponse> result = await organizationService.AddMemberAsync(userId, id, request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Cập nhật vai trò hoặc trạng thái của một thành viên trong organization.</summary>
    [HttpPatch("{id:guid}/members/{memberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchMember(Guid id, Guid memberId, PatchOrganizationMemberRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result result = await organizationService.PatchMemberAsync(userId, id, memberId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Xóa một thành viên khỏi organization.</summary>
    [HttpDelete("{id:guid}/members/{memberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMember(Guid id, Guid memberId, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result result = await organizationService.DeleteMemberAsync(userId, id, memberId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Chấp nhận lời mời tham gia organization của người dùng hiện tại.</summary>
    [HttpPost("{id:guid}/members/accept")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptInvitation(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result result = await organizationService.AcceptInvitationAsync(userId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        Guid? actorUserId = User.GetJwtSub();
        userId = actorUserId ?? Guid.Empty;
        return actorUserId is not null;
    }
}
