using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthSessionService authSessionService,
    IUserService userService) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<AccountCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AccountCreatedResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        Result<AccountCreatedResponse> result = await userService.RegisterAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpPost("login")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        Guid? userId = await userService.AuthenticateAsync(request, cancellationToken);
        if (userId is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.InvalidCredentials",
                detail: "Email hoặc mật khẩu không chính xác.");
        }

        AuthTokenPair? tokens = await authSessionService.CreateSessionAsync(userId.Value, cancellationToken);
        if (tokens is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.InactiveAccount",
                detail: "Tài khoản không tồn tại hoặc đã bị khóa.");
        }

        return Ok(tokens.ToDto());
    }

    [HttpPost("refresh")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        AuthTokenPair? tokens = await authSessionService.RefreshAsync(request.RefreshToken, cancellationToken);
        if (tokens is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.InvalidRefreshToken",
                detail: "Refresh token không hợp lệ hoặc đã hết hạn.");
        }

        return Ok(tokens.ToDto());
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        await authSessionService.RevokeByRefreshTokenAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    private ObjectResult NotImplementedYet() => Problem(
        statusCode: StatusCodes.Status501NotImplemented,
        title: "Auth.NotImplemented",
        detail: "Endpoint chưa nối logic.");
}
