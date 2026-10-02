using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Endpoint quản trị dành cho admin để tra cứu và quản lý organization.</summary>
[ApiController]
[Route("admin/organizations")]
[Produces("application/json")]
[Authorize]
public sealed class AdminOrganizationsController(
    IOrganizationService organizationService,
    IOrganizationReviewService organizationReviewService) : ControllerBase
{
    /// <summary>Lấy danh sách organization có phân trang, tìm kiếm và lọc trạng thái.</summary>
    /// <param name="request">Tham số phân trang và từ khóa tìm kiếm (page, pageSize, search).</param>
    /// <param name="status">Lọc theo trạng thái tổ chức (Pending, Active, Suspended, Rejected).</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpGet]
    [Authorize(Policy = Permissions.Organizations.Read)]
    [ProducesResponseType<PagedResult<OrganizationSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<OrganizationSummaryResponse>>> List(
        [FromQuery] PageRequest request,
        [FromQuery] OrganizationStatus? status,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result<PagedResult<OrganizationSummaryResponse>> result = await organizationService.ListAsync(adminId, request, status, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy chi tiết một organization theo id dành cho admin.</summary>
    /// <param name="id">Id của organization.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.Organizations.Read)]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrganizationResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result<OrganizationResponse> result = await organizationService.GetAdminByIdAsync(adminId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Đổi tên organization.</summary>
    /// <param name="id">Id của organization.</param>
    /// <param name="request">Thông tin tên mới.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.Organizations.Update)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateName(
        Guid id,
        [FromBody] UpdateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result result = await organizationService.UpdateNameAsync(adminId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Khóa (suspend) một organization đang Active.</summary>
    /// <param name="id">Id của organization cần khóa.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = Permissions.Organizations.Suspend)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Suspend(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result result = await organizationService.SuspendAsync(adminId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Mở khóa (activate) một organization đang Suspended.</summary>
    /// <param name="id">Id của organization cần mở khóa.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Permissions.Organizations.Suspend)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result result = await organizationService.ActivateAsync(adminId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Duyệt hồ sơ organization đang chờ duyệt (Pending).</summary>
    /// <param name="id">Id của organization cần duyệt.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPatch("{id:guid}/approve")]
    [Authorize(Policy = Permissions.Organizations.Suspend)]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrganizationResponse>> Approve(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result<OrganizationResponse> result = await organizationReviewService.ApproveAsync(adminId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Từ chối hồ sơ organization đang chờ duyệt (Pending) kèm lý do.</summary>
    /// <param name="id">Id của organization cần từ chối.</param>
    /// <param name="request">Thông tin lý do từ chối.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPatch("{id:guid}/reject")]
    [Authorize(Policy = Permissions.Organizations.Suspend)]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrganizationResponse>> Reject(
        Guid id,
        [FromBody] RejectOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result<OrganizationResponse> result = await organizationReviewService.RejectAsync(adminId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
