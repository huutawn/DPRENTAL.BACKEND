using System.Security.Claims;
using Dprental.ReviewService.Application.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.ReviewService.Api.Security;

/// <summary>
/// Mở rộng IServiceCollection thiết lập xác thực JWT Bearer chuẩn cho ReviewService kết nối JWKS với AuthService.
/// </summary>
public static class JwtAuthenticationExtensions
{
    public const string HumanOnlyPolicy = "HumanOnly";
    public const string AdminOnlyPolicy = "AdminOnly";

    public static IServiceCollection AddReviewJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ReviewJwtOptions.SectionName);
        services.AddOptions<ReviewJwtOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient("review-auth-jwks", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ReviewJwtOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<ReviewJwksConfigurationManager>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ReviewJwtOptions>>().Value;
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("review-auth-jwks");
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new ReviewJwksConfigurationManager(client, options, timeProvider);
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ReviewJwksConfigurationManager, IOptions<ReviewJwtOptions>>((bearerOptions, configManager, jwtOptionsAccessor) =>
            {
                var options = jwtOptionsAccessor.Value;

                bearerOptions.MapInboundClaims = false;
                bearerOptions.ConfigurationManager = configManager;
                bearerOptions.RefreshOnIssuerKeyNotFound = true;
                bearerOptions.SaveToken = true;
                bearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    NameClaimType = "sub",
                    RoleClaimType = "r"
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(HumanOnlyPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var u = context.User.FindFirst("u")?.Value;
                    return !string.Equals(u, "svc", StringComparison.OrdinalIgnoreCase);
                });
            });

            options.AddPolicy(AdminOnlyPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => ReviewSecurityRules.IsAdmin(context.User));
            });
        });

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ReviewAuthorizationMiddlewareResultHandler>();

        return services;
    }
}

/// <summary>
/// Xử lý kết quả kiểm tra phân quyền và trả về ProblemDetails chuẩn RFC 7807 tại ranh giới HTTP middleware.
/// </summary>
internal sealed class ReviewAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                Title = "UNAUTHORIZED",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Yêu cầu xác thực tài khoản."
            });
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var isServiceToken = string.Equals(context.User.FindFirst("u")?.Value, "svc", StringComparison.OrdinalIgnoreCase);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Title = isServiceToken ? "FORBIDDEN_SERVICE_TOKEN" : "FORBIDDEN",
                Status = StatusCodes.Status403Forbidden,
                Detail = isServiceToken
                    ? "Service token không được phép thực hiện thao tác đánh giá."
                    : "Không có quyền thực hiện thao tác này."
            });
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
