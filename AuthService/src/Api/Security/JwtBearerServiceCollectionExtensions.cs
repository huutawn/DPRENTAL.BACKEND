using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace Dprental.AuthService.Api.Security;

/// <summary>Các extension đăng ký JWT authentication vào dependency injection container.</summary>
internal static class JwtBearerServiceCollectionExtensions
{
    /// <summary>Tên claim permission, phải khớp <c>RsaAccessTokenIssuer.PermissionClaimType</c>.</summary>
    private const string PermissionClaimType = "perm";

    /// <summary>Đăng ký JWT Bearer authentication cho API bằng key ring của Application.</summary>
    internal static IServiceCollection AddJwtBearerSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddAuthorization(options =>
        {
            // Một policy cho mỗi permission đã khai báo: yêu cầu claim "perm" chứa đúng mã đó.
            foreach (string permission in Permissions.All)
            {
                options.AddPolicy(permission, policy => policy.RequireClaim(PermissionClaimType, permission));
            }
        });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<RsaJwtKeyRing, IOptions<JwtOptions>>(ConfigureJwtBearer);

        return services;
    }

    /// <summary>Thiết lập chính sách xác thực để chỉ nhận access token RS256 của key ring này.</summary>
    private static void ConfigureJwtBearer(
    JwtBearerOptions bearerOptions,
    RsaJwtKeyRing keyRing,
    IOptions<JwtOptions> options)
    {
        JwtOptions jwtOptions = options.Value;

        // Giữ tên claim nguyên bản (sub, role) để contract JWT không bị .NET tự ánh xạ.
        bearerOptions.MapInboundClaims = false;
        bearerOptions.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token) &&
                    context.Request.Cookies.TryGetValue("accessToken", out string? accessToken))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },

            // Chặn ngay sau khi chữ ký/thời hạn token hợp lệ, nhưng trước khi vào Controller:
            // kiểm tra user còn tồn tại và đang active (áp dụng Kill Switch cho mọi request).
            OnTokenValidated = async context =>
{
    string? userIdClaim = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

    if (string.IsNullOrWhiteSpace(userIdClaim) || !Guid.TryParse(userIdClaim, out Guid userId))
    {
        context.Fail("Token thiếu hoặc chứa thông tin định danh người dùng không hợp lệ.");
        return;
    }

    var userRepository = context.HttpContext.RequestServices
        .GetRequiredService<IUserRepository>();

    var user = await userRepository.GetByIdAsync(userId);

    if (user is null || !user.IsActive)
    {
        context.Fail("Tài khoản không tồn tại hoặc đã bị khóa.");
    }
},

            // Tùy chỉnh response body cho đúng format ProblemDetails hiện tại của hệ thống.
            OnChallenge = async context =>
            {
                // Nếu đã có response khác được set (VD lỗi khác), không ghi đè.
                if (context.Handled)
                {
                    return;
                }

                context.HandleResponse();

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";

                var problem = new
                {
                    type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                    title = "Unauthorized",
                    status = 401,
                    detail = context.AuthenticateFailure?.Message ?? "Không có quyền truy cập.",
                    traceId = context.HttpContext.TraceIdentifier
                };

                await context.Response.WriteAsJsonAsync(problem);
            }
        };

        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, keyId, _) => keyRing.ResolveVerificationKeys(keyId),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
    }
}
