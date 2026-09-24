using System.Security.Claims;
using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Đăng ký, xác thực và quản lý phiên đăng nhập của người dùng hoặc service.</summary>
[ApiController]
[Route("")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthSessionService authSessionService,
    IUserService userService,
    IServiceIdentityService serviceIdentityService,
    IExternalLoginService externalLoginService,
    IOptions<JwtOptions> jwtOptions,
    IOptions<GoogleAuthOptions> googleOptions,
    IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>Tạo tài khoản người dùng mới.</summary>
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

    /// <summary>Tạo tài khoản quản trị bằng khóa khởi tạo bảo mật.</summary>
    [HttpPost("admins")]
    [ProducesResponseType<AccountCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AccountCreatedResponse>> RegisterAdmin(
        RegisterAdminRequest request,
        [FromHeader(Name = "X-Admin-Creation-Key")] string? adminCreationKey,
        CancellationToken cancellationToken)
    {
        Result<AccountCreatedResponse> result = await userService.RegisterAdminAsync(request, adminCreationKey, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }


    /// <summary>Đăng nhập bằng email và mật khẩu, rồi thiết lập cookie phiên.</summary>
    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        Guid? userId = await userService.AuthenticateAsync(request, cancellationToken);
        if (userId is null)
        {
            return this.ToProblem(AuthErrors.Auth.InvalidCredentials);
        }

        AuthTokenPair? tokens = await authSessionService.CreateSessionAsync(userId.Value, cancellationToken);
        if (tokens is null)
        {
            return this.ToProblem(AuthErrors.Auth.InactiveAccount);
        }
        SetAuthCookies(tokens.AccessToken, tokens.RefreshToken);

        return Ok(new { Message = "Đăng nhập thành công.", UserId = userId });
    }

    /// <summary>Làm mới phiên đăng nhập bằng refresh token trong cookie.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue("refreshToken", out var currentRefreshToken))
        {
            return this.ToProblem(AuthErrors.Auth.MissingCookie);
        }

        AuthTokenPair? tokens = await authSessionService.RefreshAsync(currentRefreshToken, cancellationToken);
        if (tokens is null)
        {
            return this.ToProblem(AuthErrors.Auth.InvalidRefreshToken);
        }

        // Cập nhật lại Cookie với cặp token mới
        SetAuthCookies(tokens.AccessToken, tokens.RefreshToken);

        return Ok(new { Message = "Làm mới token thành công" });
    }

    /// <summary>Đăng xuất, thu hồi refresh token hiện tại và xóa cookie phiên.</summary>
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
        AuthCookies.Clear(Response, Request, environment);

        return NoContent();
    }

    /// <summary>Cấp access token OAuth2 client_credentials cho service, không đại diện người dùng.</summary>
    [HttpPost("service-token")]
    [ProducesResponseType<ServiceTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ServiceTokenResponse>> ServiceToken(ServiceTokenRequest request, CancellationToken cancellationToken)
    {
        Result<ServiceTokenResponse> result = await serviceIdentityService.IssueTokenAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Đổi ticket Google dùng một lần thành cặp access và refresh token cho Next BFF.</summary>
    [HttpPost("google/exchange")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthTokenResponse>> ExchangeGoogleLoginTicket(
        [FromBody] GoogleLoginTicketExchangeRequest? request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!googleOptions.Value.IsConfigured)
        {
            return NotFound();
        }
        if (string.IsNullOrWhiteSpace(request?.Ticket))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Auth.MissingGoogleLoginTicket",
                detail: "Ticket là bắt buộc.");
        }

        AuthTokenPair? tokens = await authSessionService.ExchangeGoogleLoginTicketAsync(request.Ticket, cancellationToken);
        if (tokens is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Auth.InvalidGoogleLoginTicket",
                detail: "Ticket không hợp lệ, đã hết hạn hoặc đã được sử dụng.");
        }

        return Ok(new AuthTokenResponse(tokens.AccessToken, tokens.RefreshToken));
    }

    /// <summary>Bắt đầu đăng nhập Google sau khi kiểm tra return URL an toàn.</summary>
    [HttpGet("google/login")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GoogleLogin([FromQuery] string? returnUrl)
    {
        GoogleAuthOptions google = googleOptions.Value;
        if (!google.IsConfigured)
        {
            return NotFound();
        }

        string target = returnUrl ?? (google.AllowedReturnUrls.Count > 0 ? google.AllowedReturnUrls[0] : "/");
        if (!ReturnUrlValidator.IsAllowed(target, google.AllowedReturnUrls))
        {
            return InvalidReturnUrl();
        }
        if (!GoogleBffCallbackResolver.TryResolve(target, google, out _))
        {
            return InvalidReturnUrl();
        }

        // Sau khi handler Google xử lý xong /signin-google, trình duyệt được chuyển tiếp tới GoogleCallback.
        AuthenticationProperties properties = new()
        {
            RedirectUri = Url.Action(nameof(GoogleCallback), new { returnUrl = target })
        };

        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>Xử lý callback Google, liên kết danh tính và chuyển ticket đăng nhập về frontend.</summary>
    [HttpGet("google/callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GoogleCallback([FromQuery] string? returnUrl, CancellationToken cancellationToken)
    {
        GoogleAuthOptions google = googleOptions.Value;
        if (!google.IsConfigured)
        {
            return NotFound();
        }

        if (!ReturnUrlValidator.IsAllowed(returnUrl, google.AllowedReturnUrls))
        {
            return InvalidReturnUrl();
        }
        if (!GoogleBffCallbackResolver.TryResolve(returnUrl, google, out string bffCallbackUrl))
        {
            return InvalidReturnUrl();
        }

        // Cookie tạm chỉ dùng một lần: đọc xong là xóa ngay dù kết quả thế nào.
        AuthenticateResult external = await HttpContext.AuthenticateAsync(GoogleAuthenticationExtensions.ExternalScheme);
        await HttpContext.SignOutAsync(GoogleAuthenticationExtensions.ExternalScheme);

        ClaimsPrincipal? principal = external.Principal;
        string? subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!external.Succeeded || principal is null || string.IsNullOrWhiteSpace(subject))
        {
            return RedirectToBff(WithError(bffCallbackUrl, returnUrl, "Auth.ExternalFailed"));
        }

        ExternalIdentity identity = new(
            GoogleDefaults.AuthenticationScheme,
            subject,
            principal.FindFirstValue(ClaimTypes.Email),
            string.Equals(principal.FindFirstValue(GoogleClaims.EmailVerified), bool.TrueString, StringComparison.OrdinalIgnoreCase),
            principal.FindFirstValue(ClaimTypes.Name));

        Result<Guid> result = await externalLoginService.SignInAsync(identity, cancellationToken);
        if (result.IsFailure)
        {
            return RedirectToBff(WithError(bffCallbackUrl, returnUrl, result.Error.Code));
        }

        string? ticket = await authSessionService.CreateGoogleLoginTicketAsync(result.Value, cancellationToken);
        if (ticket is null)
        {
            return RedirectToBff(WithError(bffCallbackUrl, returnUrl, "Auth.InactiveAccount"));
        }

        return RedirectToBff(WithTicket(bffCallbackUrl, returnUrl, ticket));
    }

    private ObjectResult InvalidReturnUrl() => Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Auth.InvalidReturnUrl",
        detail: "returnUrl không nằm trong danh sách được phép.");

    /// <summary>Gắn mã lỗi vào query của returnUrl; lúc này user đang ở trên trình duyệt nên chuyển hướng thay vì trả JSON.</summary>
    private static string WithError(string callbackUrl, string returnUrl, string errorCode) =>
        QueryHelpers.AddQueryString(callbackUrl, new Dictionary<string, string?>
        {
            ["error"] = errorCode,
            ["returnUrl"] = returnUrl
        });

    private static string WithTicket(string callbackUrl, string returnUrl, string ticket) =>
        QueryHelpers.AddQueryString(callbackUrl, new Dictionary<string, string?>
        {
            ["ticket"] = ticket,
            ["returnUrl"] = returnUrl
        });

    private RedirectResult RedirectToBff(string destination)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Redirect(destination);
    }
    /// <summary>Yêu cầu đặt lại mật khẩu mà không tiết lộ email có tồn tại hay không.</summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword([FromBody] RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        var result = await userService.RequestPasswordResetAsync(request, cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        // Luôn trả về thông báo thành công chung chung để chống dò quét email (Email Enumeration)
        return Ok(new { Message = "Yêu cầu khôi phục mật khẩu đã được xử lý." });
    }

    /// <summary>Đặt lại mật khẩu bằng token hợp lệ và thu hồi toàn bộ phiên cũ.</summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await userService.ResetPasswordAsync(request, cancellationToken);
        if (result.IsFailure) return this.ToProblem(result.Error);
        return Ok(new { Message = "Đã đặt lại mật khẩu và hủy toàn bộ phiên cũ." });
    }

    /// <summary>Thu hồi toàn bộ phiên đăng nhập của người dùng hiện tại.</summary>
    [Authorize]
    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAllSessions(CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is Guid userId)
        {
            await authSessionService.RevokeAllSessionsForUserAsync(userId, cancellationToken);
        }
        return NoContent();
    }

    private ObjectResult NotImplementedYet() => Problem(
        statusCode: StatusCodes.Status501NotImplemented,
        title: "Auth.NotImplemented",
        detail: "Endpoint chưa nối logic.");

    private void SetAuthCookies(string accessToken, string refreshToken)
    {
        AuthCookies.SetAuthCookies(
            Response,
            Request,
            environment,
            accessToken,
            refreshToken,
            jwtOptions.Value.AccessTokenLifetimeMinutes);
    }
}
