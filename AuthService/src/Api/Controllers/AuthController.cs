using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthSessionService authSessionService,
    IUserService userService,
    IOptions<JwtOptions> jwtOptions,
    IWebHostEnvironment environment) : ControllerBase
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
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
        SetAuthCookies(tokens.AccessToken, tokens.RefreshToken);

        return Ok(new { Message = "Đăng nhập thành công.", UserId = userId});
    }

    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue("refreshToken", out var currentRefreshToken))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.MissingCookie",
                detail: "Không tìm thấy refresh token trong cookie.");
        }

        AuthTokenPair? tokens = await authSessionService.RefreshAsync(currentRefreshToken, cancellationToken);
        if (tokens is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.InvalidRefreshToken",
                detail: "Refresh token không hợp lệ hoặc đã hết hạn.");
        }

        // Cập nhật lại Cookie với cặp token mới
        SetAuthCookies(tokens.AccessToken, tokens.RefreshToken);

        return Ok(new { Message = "Làm mới token thành công" });
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        // Đọc token từ Cookie để thu hồi trong Database
        if (Request.Cookies.TryGetValue("refreshToken", out var currentRefreshToken))
        {
            await authSessionService.RevokeByRefreshTokenAsync(currentRefreshToken, cancellationToken);
        }

        // Xóa Cookie ở phía client
        Response.Cookies.Delete("accessToken", CreateCookieOptions(TimeSpan.Zero));
        Response.Cookies.Delete("refreshToken", CreateCookieOptions(TimeSpan.Zero));

        return NoContent();
    }

    private ObjectResult NotImplementedYet() => Problem(
        statusCode: StatusCodes.Status501NotImplemented,
        title: "Auth.NotImplemented",
        detail: "Endpoint chưa nối logic.");

    private void SetAuthCookies(string accessToken, string refreshToken)
    {
        Response.Cookies.Append(
            "accessToken",
            accessToken,
            CreateCookieOptions(TimeSpan.FromMinutes(jwtOptions.Value.AccessTokenLifetimeMinutes)));
        Response.Cookies.Append(
            "refreshToken",
            refreshToken,
            CreateCookieOptions(TimeSpan.FromDays(30)));
    }

    private CookieOptions CreateCookieOptions(TimeSpan lifetime)
    {
        bool isHttps = Request.IsHttps;
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = environment.IsProduction() || isHttps,
            SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(lifetime)
        };
    }
}
