using System.Security.Claims;
using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
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
    IFirebaseLoginService firebaseLoginService,
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
        Result<Guid> authResult = await userService.AuthenticateAsync(request, cancellationToken);
        if (authResult.IsFailure)
        {
            return this.ToProblem(authResult.Error);
        }

        Result<AuthTokenPair> sessionResult = await authSessionService.CreateSessionAsync(authResult.Value, cancellationToken);
        if (sessionResult.IsFailure)
        {
            return this.ToProblem(sessionResult.Error);
        }
        SetAuthCookies(sessionResult.Value.AccessToken, sessionResult.Value.RefreshToken);

        return Ok(new { Message = "Đăng nhập thành công.", UserId = authResult.Value });
    }

    /// <summary>Đăng nhập Google bằng Firebase ID token do Next BFF gửi lên.</summary>
    [HttpPost("firebase/login")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthTokenResponse>> FirebaseLogin(
        [FromBody] FirebaseLoginRequest? request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        Result<AuthTokenResponse> result = await firebaseLoginService.LoginAsync(
            request?.IdToken,
            cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
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

        Result<AuthTokenPair> result = await authSessionService.RefreshAsync(currentRefreshToken, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        // Cập nhật lại Cookie với cặp token mới
        SetAuthCookies(result.Value.AccessToken, result.Value.RefreshToken);
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

        Result<AuthTokenPair> tokensResult = await authSessionService.ExchangeGoogleLoginTicketAsync(request.Ticket, cancellationToken);
        if (tokensResult.IsFailure)
        {
            return this.ToProblem(tokensResult.Error);
        }

        return Ok(new AuthTokenResponse(tokensResult.Value.AccessToken, tokensResult.Value.RefreshToken));
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
            if (result.Error.Code == AuthErrorCode.Auth.Uncategorized)
            {
                return this.ToProblem(result.Error);
            }
            return RedirectToBff(WithError(bffCallbackUrl, returnUrl, result.Error.Code));
        }

        Result<string> ticketResult = await authSessionService.CreateGoogleLoginTicketAsync(result.Value, cancellationToken);
        if (ticketResult.IsFailure)
        {
            if (ticketResult.Error.Code == AuthErrorCode.Auth.Uncategorized)
            {
                return this.ToProblem(ticketResult.Error);
            }
            return RedirectToBff(WithError(bffCallbackUrl, returnUrl, ticketResult.Error.Code));
        }

        return RedirectToBff(WithTicket(bffCallbackUrl, returnUrl, ticketResult.Value));
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
    /// <summary>Yêu cầu đặt lại mật khẩu. Development: khóa admin hợp lệ trong X-Admin-Creation-Key trả token để thử nghiệm.</summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] RequestPasswordResetRequest request,
        [FromHeader(Name = "X-Admin-Creation-Key")] string? testKey,
        CancellationToken cancellationToken)
    {
        Result<string?> result = await userService.RequestPasswordResetAsync(
            request, environment.IsDevelopment() ? testKey : null, cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        if (result.Value is string token)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(new { Message = "Yêu cầu khôi phục mật khẩu đã được xử lý.", Token = token });
        }

        return Ok(new { Message = "Yêu cầu khôi phục mật khẩu đã được xử lý." });
    }

    /// <summary>Xác thực link từ email, gắn token vào Cookie và chuyển hướng về Frontend.</summary>
    [HttpGet("verify-reset-link")]
    public IActionResult VerifyResetLink([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { Message = "Link khôi phục không hợp lệ hoặc đã hỏng." });
        }

        // Tạo HttpOnly Cookie chứa token
        CookieOptions cookieOptions = new()
        {
            HttpOnly = true,
            Secure = true, // Đảm bảo Cookie chỉ gửi qua HTTPS
            SameSite = SameSiteMode.Strict, // Chống tấn công CSRF
            Expires = DateTimeOffset.UtcNow.AddMinutes(30) // Khớp với TTL của Token trong DB
        };

        Response.Cookies.Append("ResetPasswordToken", token, cookieOptions);

        // Chuyển hướng user về trang giao diện frontend để họ nhập mật khẩu mới.
        // CẦN THAY ĐỔI: Sửa URL dưới đây thành địa chỉ thật của Frontend team bạn
        return Redirect("https://localhost:3000/auth/create-new-password");
    }

    /// <summary>Đặt lại mật khẩu bằng token hợp lệ và thu hồi toàn bộ phiên cũ.</summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        // 1. Trích xuất Token từ HttpOnly Cookie do trình duyệt tự động gửi lên
        if (!Request.Cookies.TryGetValue("ResetPasswordToken", out string? resetToken) || string.IsNullOrWhiteSpace(resetToken))
        {
            // Sử dụng cấu trúc Error tương tự như các lỗi khác trong hệ thống của bạn
            return Unauthorized(new { Message = "Phiên đặt lại mật khẩu không hợp lệ hoặc đã hết hạn." });
        }

        // 2. Truyền resetToken vào vị trí tham số đầu tiên
        var result = await userService.ResetPasswordAsync(resetToken, request, cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        // 3. Xóa dọn Cookie chứa Token sau khi đã đổi mật khẩu thành công
        Response.Cookies.Delete("ResetPasswordToken");

        return Ok(new { Message = "Đã đặt lại mật khẩu và hủy toàn bộ phiên cũ." });
    }

    /// <summary>Thu hồi một phiên đăng nhập cụ thể của người dùng (Đăng xuất khỏi thiết bị khác).</summary>
    [Authorize]
    [HttpPost("revoke-session")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeDeviceSession([FromBody] RevokeDeviceSessionRequest request, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is Guid userId)
        {
            // Gọi hàm đã có sẵn ở dưới cùng của Service
            var result = await authSessionService.RevokeSessionAsync(userId, request.SessionId, cancellationToken);

            if (result.IsFailure)
            {
                return this.ToProblem(result.Error);
            }

            return NoContent();
        }

        return Unauthorized();
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
