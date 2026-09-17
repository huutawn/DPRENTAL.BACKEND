using Dprental.AuthService.Api.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
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
    public ActionResult<AuthTokenResponse> Refresh(RefreshTokenRequest request) => NotImplementedYet();

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Logout(RefreshTokenRequest request) => NotImplementedYet();

    private ObjectResult NotImplementedYet() => Problem(
        statusCode: StatusCodes.Status501NotImplemented,
        title: "Auth.NotImplemented",
        detail: "Endpoint chưa nối logic.");
}
