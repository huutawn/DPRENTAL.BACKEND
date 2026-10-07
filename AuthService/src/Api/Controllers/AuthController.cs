using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    IFirebaseLoginService firebaseLoginService,
    IOptions<JwtOptions> jwtOptions,
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
    [ApiExplorerSettings(IgnoreApi = true)]
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
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType<ServiceTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ServiceTokenResponse>> ServiceToken(ServiceTokenRequest request, CancellationToken cancellationToken)
    {
        Result<ServiceTokenResponse> result = await serviceIdentityService.IssueTokenAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Yêu cầu đặt lại mật khẩu qua mã OTP gửi về email.</summary>
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

        if (result.Value is string otp)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(new { Message = "Mã OTP khôi phục mật khẩu đã được gửi.", Otp = otp, Token = otp });
        }

        return Ok(new { Message = "Mã OTP khôi phục mật khẩu đã được gửi." });
    }

    /// <summary>Xác thực mã OTP khôi phục mật khẩu, gắn token vào HttpOnly Cookie để tiến hành đổi mật khẩu.</summary>
    [HttpPost("verify-reset-otp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyResetOtp(
        [FromBody] VerifyResetOtpRequest request,
        CancellationToken cancellationToken)
    {
        Result<string> result = await userService.VerifyResetOtpAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        string resetToken = result.Value;

        CookieOptions cookieOptions = new()
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddMinutes(15)
        };

        Response.Cookies.Append("ResetPasswordToken", resetToken, cookieOptions);

        return Ok(new { Message = "Xác thực mã OTP thành công.", ResetToken = resetToken });
    }

    /// <summary>Hỗ trợ xác thực mã OTP qua GET query parameter.</summary>
    [HttpGet("verify-reset-otp")]
    public async Task<IActionResult> VerifyResetOtpFromQuery(
        [FromQuery] string? otp,
        [FromQuery] string? email,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(otp))
        {
            return BadRequest(new { Message = "Mã OTP là bắt buộc." });
        }

        return await VerifyResetOtp(new VerifyResetOtpRequest(otp, email), cancellationToken);
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
