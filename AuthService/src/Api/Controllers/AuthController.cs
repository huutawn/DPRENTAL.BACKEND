using AuthService.Application.Authentication.Sessions;
using Dprental.AuthService.Api.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(IAuthenticationSessionService authenticationSessionService) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<AccountCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<AccountCreatedResponse> Register(RegisterRequest request) => NotImplementedYet();

    [HttpPost("login")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<AuthTokenResponse> Login(LoginRequest request) => NotImplementedYet();

    [HttpPost("refresh")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        AuthenticationTokenPair? tokens = await authenticationSessionService.RefreshAsync(request.RefreshToken, cancellationToken);
        if (tokens is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.InvalidRefreshToken",
                detail: "Refresh token không hợp lệ hoặc đã hết hạn.");
        }

        return Ok(new AuthTokenResponse(tokens.AccessToken, tokens.RefreshToken));
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        await authenticationSessionService.RevokeByRefreshTokenAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    private ObjectResult NotImplementedYet() => Problem(
        statusCode: StatusCodes.Status501NotImplemented,
        title: "Auth.NotImplemented",
        detail: "Endpoint chưa nối logic.");
}
