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
    /// <summary>Lấy danh sách organization có phân trang, tìm kiếm và lọc theo trạng thái, loại, tỉnh/thành, quá hạn duyệt hoặc đã nhắc duyệt.</summary>
    /// <param name="request">Tham số phân trang và từ khóa tìm kiếm (page, pageSize, search).</param>
    /// <param name="status">Lọc theo trạng thái tổ chức (Pending, Active, Suspended, Rejected).</param>
    /// <param name="type">Lọc theo loại tổ chức (Supplier, Renter, Platform).</param>
    /// <param name="province">Lọc theo tỉnh/thành phố.</param>
    /// <param name="overdueOnly">Chỉ lấy hồ sơ Pending đã quá hạn duyệt.</param>
    /// <param name="remindedOnly">Chỉ lấy hồ sơ Pending đã được Owner gửi nhắc duyệt.</param>
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
        [FromQuery] OrganizationType? type = null,
        [FromQuery] string? province = null,
        [FromQuery] bool overdueOnly = false,
        [FromQuery] bool remindedOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result<PagedResult<OrganizationSummaryResponse>> result = await organizationService.ListAsync(
            adminId,
            request,
            status,
            type,
            province,
            overdueOnly,
            remindedOnly,
            cancellationToken);
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

    /// <summary>Cập nhật hồ sơ liên hệ và hiển thị của organization.</summary>
    /// <param name="id">Id của organization.</param>
    /// <param name="request">Thông tin liên hệ và hiển thị mới.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.Organizations.Update)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result result = await organizationService.UpdateProfileAsync(adminId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Khóa (suspend) một organization đang Active kèm lý do.</summary>
    /// <param name="id">Id của organization cần khóa.</param>
    /// <param name="request">Thông tin lý do khóa (bắt buộc, tối đa 500 ký tự).</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = Permissions.Organizations.Suspend)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Suspend(
        Guid id,
        [FromBody] SuspendOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result result = await organizationService.SuspendAsync(adminId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Lấy nhật ký lịch sử của organization có phân trang và bộ lọc tìm kiếm dành cho Admin.</summary>
    /// <param name="id">Id của organization.</param>
    /// <param name="request">Tham số phân trang và từ khóa tìm kiếm (page, pageSize, search).</param>
    /// <param name="action">Lọc theo hành động (khớp chính xác, không phân biệt hoa thường).</param>
    /// <param name="status">Lọc theo trạng thái tổ chức sau sự kiện (Pending, Active, Suspended, Rejected).</param>
    /// <param name="result">Lọc theo kết quả (khớp chính xác, không phân biệt hoa thường).</param>
    /// <param name="reason">Lọc theo lý do (tìm chuỗi con, không phân biệt hoa thường).</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpGet("{id:guid}/history")]
    [Authorize(Policy = Permissions.Organizations.Read)]
    [ProducesResponseType<PagedResult<OrganizationHistoryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<OrganizationHistoryResponse>>> GetHistory(
        Guid id,
        [FromQuery] PageRequest request,
        [FromQuery] string? action = null,
        [FromQuery] OrganizationStatus? status = null,
        [FromQuery] string? result = null,
        [FromQuery] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (User.GetJwtSub() is not Guid adminId) return Unauthorized();

        Result<PagedResult<OrganizationHistoryResponse>> historyResult = await organizationService.GetHistoryAsync(
            adminId,
            id,
            request,
            action,
            status,
            result,
            reason,
            cancellationToken);
        return historyResult.IsFailure ? this.ToProblem(historyResult.Error) : Ok(historyResult.Value);
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
