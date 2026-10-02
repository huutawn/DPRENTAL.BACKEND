using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Application.DTOs.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>
/// Quản lý organization mà người dùng được phép truy cập. MVP: mỗi organization chỉ có Owner (người đăng ký),
/// chưa có API quản lý thành viên/lời mời.
/// </summary>
[ApiController]
[Route("organizations")]
[Produces("application/json")]
[Authorize]
public sealed class OrganizationsController(
    IOrganizationService organizationService,
    IOrganizationRegistrationService organizationRegistrationService) : ControllerBase
{
    /// <summary>Người dùng đang đăng nhập chọn đăng ký tổ chức Supplier/Renter và trở thành Owner; tổ chức chờ Platform duyệt.</summary>
    [HttpPost("register")]
    [ProducesResponseType<OrganizationRegisteredResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(OrganizationProfileRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationRegisteredResponse> result = await organizationRegistrationService.RegisterForExistingUserAsync(userId, request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Tổ chức của chính người gọi, dùng để kiểm tra trạng thái hồ sơ mỗi lần vào trang: 200 kèm hồ sơ
    /// (xem <c>status</c>) nếu đã đăng ký, 204 nếu chưa. Không kèm URL xem giấy phép — lấy qua GET /{id} khi cần.
    /// </summary>
    [HttpGet("mine")]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMine(CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationResponse?> result = await organizationService.GetMineAsync(userId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return result.Value is null ? NoContent() : Ok(result.Value);
    }

    /// <summary>Lấy thông tin một organization khi người gọi là Owner.</summary>
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
    /// <summary>
    /// Owner gửi lại toàn bộ hồ sơ. Pending: sửa tự do. Rejected: sửa và tự chuyển về Pending.
    /// Active: chỉ đổi liên hệ/hiển thị — đổi thông tin pháp lý trả 400.
    /// </summary>
    [HttpPut("{id:guid}/profile")]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateProfile(Guid id, UpdateOrganizationProfileRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationResponse> result = await organizationRegistrationService.UpdateProfileAsync(userId, id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>
    /// Owner nhắc Platform duyệt hồ sơ đang chờ đã quá hạn (xem <c>canRemindAtUtc</c> trong chi tiết organization).
    /// Hồ sơ được đưa lên đầu hàng chờ; nhắc quá sớm hoặc hồ sơ không còn chờ duyệt trả 409.
    /// </summary>
    [HttpPost("{id:guid}/remind")]
    [ProducesResponseType<OrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemindReview(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out Guid userId))
        {
            return Unauthorized();
        }

        Result<OrganizationResponse> result = await organizationRegistrationService.RemindReviewAsync(userId, id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        Guid? actorUserId = User.GetJwtSub();
        userId = actorUserId ?? Guid.Empty;
        return actorUserId is not null;
    }
}
