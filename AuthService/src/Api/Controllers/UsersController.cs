using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.DTOs.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dprental.AuthService.Domain.DTOs.User;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Cung cấp hồ sơ, quyền và organization của người dùng đang đăng nhập.</summary>
[ApiController]
[Route("users")]
[Produces("application/json")]
[Authorize]
public sealed class UsersController(
    IUserService userService,
    IUserAccountService userAccountService,
    IAuthSessionService authSessionService,
    IOrganizationService organizationService,
    IWebHostEnvironment environment) : ControllerBase
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

    /// <summary>Hoàn thiện thông tin định danh cá nhân lần đầu (CCCD, SĐT, ngày sinh).</summary>
    [HttpPost("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CurrentUserResponse>> CompleteProfile(
        [FromBody] CompleteProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result<CurrentUserResponse> result = await userAccountService.CompleteProfileAsync(userId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Cập nhật thông tin hồ sơ cá nhân sau khi đã hoàn thiện.</summary>
    [HttpPatch("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentUserResponse>> UpdateProfile(
        [FromBody] UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result<CurrentUserResponse> result = await userAccountService.UpdateProfileAsync(userId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Đổi mật khẩu tài khoản người dùng và hủy toàn bộ phiên đăng nhập cũ.</summary>
    [HttpPost("me/change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result result = await userService.ChangePasswordAsync(userId, request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        AuthCookies.Clear(Response, Request, environment);
        return NoContent();
    }

    /// <summary>Lấy danh sách các phiên đăng nhập đang hoạt động của người dùng.</summary>
    [HttpGet("me/sessions")]
    [ProducesResponseType<IReadOnlyList<SessionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> GetSessions(CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Request.Cookies.TryGetValue(AuthCookies.RefreshTokenCookieName, out string? currentRefreshToken);
        Result<IReadOnlyList<SessionResponse>> sessionsResult = await authSessionService.GetSessionsAsync(userId, currentRefreshToken, cancellationToken);
        if (sessionsResult.IsFailure)
        {
            return this.ToProblem(sessionsResult.Error);
        }
        return Ok(sessionsResult.Value);
    }

    /// <summary>Thu hồi một phiên đăng nhập cụ thể của người dùng.</summary>
    [HttpDelete("me/sessions/{sessionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(
        [FromRoute] Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Request.Cookies.TryGetValue(AuthCookies.RefreshTokenCookieName, out string? currentRefreshToken);
        Result<Guid> currentSessionIdResult = await authSessionService.GetCurrentSessionIdAsync(currentRefreshToken, cancellationToken);
        Guid? currentSessionId = currentSessionIdResult.IsSuccess ? currentSessionIdResult.Value : null;

        Result result = await authSessionService.RevokeSessionAsync(userId, sessionId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        if (currentSessionId.HasValue && currentSessionId.Value == sessionId)
        {
            AuthCookies.Clear(Response, Request, environment);
        }

        return NoContent();
    }

    /// <summary>Lấy danh sách các tài khoản đăng nhập bên ngoài (Google,...) đã liên kết.</summary>
    [HttpGet("me/external-logins")]
    [ProducesResponseType<IReadOnlyList<ExternalLoginResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<ExternalLoginResponse>>> GetExternalLogins(CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result<IReadOnlyList<ExternalLoginResponse>> result = await userAccountService.GetExternalLoginsAsync(userId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Liên kết tài khoản đăng nhập bên ngoài (Google,...) vào tài khoản hiện tại.</summary>
    [HttpPost("me/external-logins/{provider}/link")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> LinkExternalLogin(
        [FromRoute] string provider,
        [FromBody] LinkExternalLoginRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result result = await userAccountService.LinkExternalLoginAsync(userId, provider, request.IdToken, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Hủy liên kết tài khoản đăng nhập bên ngoài (Google,...).</summary>
    [HttpDelete("me/external-logins/{provider}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnlinkExternalLogin(
        [FromRoute] string provider,
        CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result result = await userAccountService.UnlinkExternalLoginAsync(userId, provider, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>Tự vô hiệu hóa tài khoản cá nhân (xóa mềm) và đăng xuất khỏi toàn bộ thiết bị.</summary>
    [HttpDelete("me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeactivateAccount(CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result result = await userAccountService.DeactivateAsync(userId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        AuthCookies.Clear(Response, Request, environment);
        return NoContent();
    }

    /// <summary>Lấy danh sách các organization mà người dùng hiện tại đang tham gia (Active).</summary>
    [HttpGet("me/organizations")]
    [ProducesResponseType<IReadOnlyList<MyOrganizationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<MyOrganizationResponse>>> GetMyOrganizations(CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid userId)
        {
            return Unauthorized();
        }

        Result<IReadOnlyList<MyOrganizationResponse>> result = await organizationService.GetMyOrganizationsAsync(userId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Cập nhật hồ sơ (tên, số điện thoại, avatar) của người dùng hiện tại.</summary>
    /// <param name="request">Thông tin hồ sơ cần cập nhật.</param>
    /// <param name="cancellationToken">Token hủy request.</param>
    [HttpPut("me")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateCurrentUser([FromBody] UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.Identity?.Name, out Guid userId))
        {
            return Unauthorized();
        }

        Result result = await userService.UpdateUserProfileAsync(userId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }
}
