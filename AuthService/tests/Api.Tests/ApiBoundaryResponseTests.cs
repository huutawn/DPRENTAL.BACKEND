using AuthService.Application.DTOs.Auth;
using AuthService.Application.DTOs.User;
using AuthService.Domain.DTOs.User;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

/// <summary>
/// Test API boundary:
/// - HTTP statuses: 400/401/403/404/409/500.
/// - Đăng nhập không tiết lộ email tồn tại (trả 401 cho mọi trường hợp sai tài khoản).
/// - Cookie phiên: login đặt cookie, logout xóa cookie.
/// - JWT middleware: kiểm tra user active, lỗi hạ tầng trả 500 không nhầm thành 401.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class ApiBoundaryResponseTests
{
    private const string BootstrapKey = "test-bootstrap-creation-key";

    [Fact]
    public async Task ForgotPassword_DevelopmentWithValidKey_ReturnsUsableToken()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(environmentName: "Development");
        Guid userId = await SeedUserAsync(factory, "reset@example.com", "OldPassword123!", isActive: true);
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Creation-Key", BootstrapKey);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password", new RequestPasswordResetRequest("reset@example.com"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string token = body.RootElement.GetProperty("token").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(token));

        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        RecoveryToken saved = await db.RecoveryTokens.SingleAsync(t => t.UserId == userId);
        Assert.Equal(RefreshTokenUtility.ComputeHash(token), saved.TokenHash);

        using HttpRequestMessage reset = new(HttpMethod.Post, "/api/v1/auth/reset-password")
        {
            Content = JsonContent.Create(new ResetPasswordRequest("NewPassword123!", "NewPassword123!"))
        };
        reset.Headers.Add("Cookie", $"ResetPasswordToken={token}");
        using HttpResponseMessage resetResponse = await client.SendAsync(reset);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.RecoveryTokens.SingleAsync(t => t.UserId == userId)).UsedAtUtc);
    }

    [Theory]
    [InlineData("Development", null, "reset@example.com")]
    [InlineData("Development", "wrong-key", "reset@example.com")]
    [InlineData("Production", BootstrapKey, "reset@example.com")]
    [InlineData("Development", BootstrapKey, "missing@example.com")]
    [InlineData("Development", BootstrapKey, "inactive@example.com")]
    public async Task ForgotPassword_WithoutAuthorizedActiveAccount_HidesToken(
        string environmentName, string? key, string email)
    {
        using WebApplicationFactory<Program> factory = CreateFactory(environmentName: environmentName);
        await SeedUserAsync(factory, "reset@example.com", "OldPassword123!", isActive: true);
        await SeedUserAsync(factory, "inactive@example.com", "OldPassword123!", isActive: false);
        using HttpClient client = factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Admin-Creation-Key", key);
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password", new RequestPasswordResetRequest(email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task ForgotPassword_InvalidEmail_ReturnsBadRequestWithoutToken()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(environmentName: "Development");
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Creation-Key", BootstrapKey);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password", new RequestPasswordResetRequest("invalid-email"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Login_InvalidEmailOrPassword_DoesNotDiscloseEmailExistence()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        // 1. Email hoàn toàn không tồn tại -> 401 InvalidCredentials
        HttpResponseMessage response1 = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "nonexistent@example.com",
            Password: "Password123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, response1.StatusCode);
        var problem1 = await response1.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrorCode.Auth.InvalidCredentials, problem1?.Title);

        // 2. Tạo user nhưng gõ sai mật khẩu -> cũng 401 InvalidCredentials
        Guid userId = await SeedUserAsync(factory, "realuser@example.com", "CorrectPassword123!", isActive: true);

        HttpResponseMessage response2 = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "realuser@example.com",
            Password: "WrongPassword123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, response2.StatusCode);
        var problem2 = await response2.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrorCode.Auth.InvalidCredentials, problem2?.Title);

        // 3. User bị khóa/vô hiệu hóa -> cũng 401 InvalidCredentials (không lộ email tồn tại)
        Guid disabledUserId = await SeedUserAsync(factory, "disabled@example.com", "Password123!", isActive: false);

        HttpResponseMessage response3 = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "disabled@example.com",
            Password: "Password123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, response3.StatusCode);
        var problem3 = await response3.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrorCode.Auth.InvalidCredentials, problem3?.Title);
    }

    [Fact]
    public async Task Refresh_WithoutCookie_Returns401MissingCookie()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrorCode.Auth.MissingCookie, problem?.Title);
    }

    [Fact]
    public async Task Refresh_WithInvalidCookie_Returns401InvalidRefreshToken()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", "refreshToken=completely-invalid-token");

        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrorCode.Auth.InvalidRefreshToken, problem?.Title);
    }

    [Fact]
    public async Task Login_Success_SetsAuthCookies_AndLogout_ClearsCookies()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        await SeedUserAsync(factory, "cookieuser@example.com", "Password123!", isActive: true);

        HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "cookieuser@example.com",
            Password: "Password123!"));

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        IEnumerable<string> setCookies = loginResponse.Headers.GetValues("Set-Cookie");
        Assert.Contains(setCookies, c => c.StartsWith("accessToken=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(setCookies, c => c.StartsWith("refreshToken=", StringComparison.OrdinalIgnoreCase));

        // Logout
        HttpRequestMessage logoutRequest = new(HttpMethod.Post, "/api/v1/auth/logout");
        string refreshTokenCookie = setCookies.First(c => c.StartsWith("refreshToken=", StringComparison.OrdinalIgnoreCase)).Split(';')[0];
        logoutRequest.Headers.Add("Cookie", refreshTokenCookie);

        HttpResponseMessage logoutResponse = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        IEnumerable<string> logoutCookies = logoutResponse.Headers.GetValues("Set-Cookie");
        Assert.Contains(logoutCookies, c => c.Contains("expires=", StringComparison.OrdinalIgnoreCase) || c.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JwtMiddleware_ActiveUserCheck_InfrastructureFailure_Returns500Not401()
    {
        // Lỗi hạ tầng từ kiểm tra tài khoản phải đi qua global exception handler và trả 500.
        using WebApplicationFactory<Program> factory = CreateFactory(services =>
        {
            services.RemoveAll<IUserService>();
            services.AddScoped<IUserService, InfrastructureFailingUserService>();
        });

        Guid userId = Guid.NewGuid();
        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId, ["Admin"]));

        using HttpClient client = factory.CreateClient();
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/users/me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Value);

        HttpResponseMessage response = await client.SendAsync(request);

        // Đảm bảo không bị trả nhầm thành 401
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_ExistingEmail_Returns409Conflict()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        await SeedUserAsync(factory, "existing@example.com", "Password123!", isActive: true);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            Email: "existing@example.com",
            Password: "Password123!",
            FullName: "Another User"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrorCode.User.EmailAlreadyExists, problem?.Title);
    }

    [Fact]
    public async Task Register_KnownUniqueConstraintThrownByService_Returns409ProblemDetails()
    {
        StorageConstraintViolationException failure = new(
            StorageConstraintViolationType.Unique,
            StorageConstraints.UserEmail);
        using WebApplicationFactory<Program> factory = CreateFactory(services =>
        {
            services.RemoveAll<IUserService>();
            services.AddScoped<IUserService>(_ => new InfrastructureFailingUserService(failure));
        });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest("new@example.com", "Password123!", "New User"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrors.User.EmailAlreadyExists.Code, problem?.Title);
        Assert.Equal(AuthErrors.User.EmailAlreadyExists.Message, problem?.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_UnhandledException_ReturnsSanitized500ProblemDetails(bool unknownConstraint)
    {
        const string secret = "SensitivePassword123!";
        Exception failure = unknownConstraint
            ? new StorageConstraintViolationException(StorageConstraintViolationType.Other, "UNKNOWN_CONSTRAINT", secret)
            : new InvalidOperationException(secret);
        using WebApplicationFactory<Program> factory = CreateFactory(services =>
        {
            services.RemoveAll<IUserService>();
            services.AddScoped<IUserService>(_ => new InfrastructureFailingUserService(failure));
        }, environmentName: "Production");
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest("new@example.com", "Password123!", "New User"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("UNKNOWN_CONSTRAINT", body, StringComparison.Ordinal);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(AuthErrors.Auth.Uncategorized.Code, problem?.Title);
        Assert.Equal(AuthErrors.Auth.Uncategorized.Message, problem?.Detail);
    }

    // ========================================================================
    // HELPERS & FAILING TEST SERVICES
    // ========================================================================

    private static WebApplicationFactory<Program> CreateFactory(
        Action<IServiceCollection>? configureServices = null,
        string environmentName = "Development")
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        string publicKey = rsa.ExportRSAPublicKeyPem();
        string privateKey = rsa.ExportRSAPrivateKeyPem();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MediaService:ApiKey", "test-media-key");
            builder.UseSetting("MediaService:PublicGatewayUrl", "http://gateway.example.test");
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:AdminBootstrap:CreationKey"] = BootstrapKey,
                    ["Auth:Jwt:ActiveKeyId"] = "key-2026-01",
                    ["Auth:Jwt:Keys:0:KeyId"] = "key-2026-01",
                    ["Auth:Jwt:Keys:0:PublicKeyPem"] = publicKey,
                    ["Auth:Jwt:Keys:0:PrivateKeyPem"] = privateKey
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options =>
                    options.UseInMemoryDatabase(inMemoryDatabaseName));

                configureServices?.Invoke(services);
            });
        });
    }

    private static async Task<Guid> SeedUserAsync(WebApplicationFactory<Program> factory, string email, string password, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = email.Trim().ToLowerInvariant(),
            FullName = "Seeded User",
            CreatedAt = DateTime.UtcNow,
            HasPassword = true,
            IsActive = isActive
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static string CreateSignedJwtToken(Guid userId)
    {
        // Tạo token JWT với chữ ký giả lập
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey key = new(rsa) { KeyId = "current" };
        JwtSecurityToken token = new(
            issuer: "https://auth.example.test",
            audience: "dprental-clients",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim("p", "identity.users.read")
            ],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class InfrastructureFailingUserService(Exception? registerFailure = null) : IUserService
    {
        public Task<Result> ValidateActiveUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PostgreSQL connection pool exhausted");
        public Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) => throw registerFailure ?? new NotSupportedException();
        public Task<Result<AccountCreatedResponse>> RegisterAdminAsync(RegisterAdminRequest request, string? creationKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<AdminUserDetailDto>> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> SuspendUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ActivateUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Guid>> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<string?>> RequestPasswordResetAsync(RequestPasswordResetRequest request, string? testKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ResetPasswordAsync(string token, ResetPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<PagedResult<AdminUserListItemDto>>> GetUsersPagedAsync(GetUsersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<RoleDto>>> GetAllRolesAsync(CancellationToken cancellationToken = default)
        {
            // Trả về một list rỗng để test có thể compile và pass
            return Task.FromResult(Result.Success<IReadOnlyList<RoleDto>>([]));
        }
    }
}
