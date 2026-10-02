using System.IdentityModel.Tokens.Jwt;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Security.Jwt;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.CatalogService.Api.Security;

/// <summary>Các extension đăng ký JWT Bearer authentication vào dependency injection container.</summary>
internal static class JwtBearerServiceCollectionExtensions
{
    internal static IServiceCollection AddJwtBearerSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        JwtOptions jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new();
        RsaJwtKeyRing keyRing = new(jwtOptions);

        services.AddSingleton(keyRing);
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(bearerOptions =>
            {
                // Giữ tên claim nguyên bản (sub, r, sid, u)
                bearerOptions.MapInboundClaims = false;
                bearerOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Ưu tiên Authorization header; fallback sang cookie accessToken
                        string? authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(authHeader) &&
                            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Token = authHeader["Bearer ".Length..].Trim();
                            return Task.CompletedTask;
                        }

                        if (string.IsNullOrWhiteSpace(context.Token) &&
                            context.Request.Cookies.TryGetValue("accessToken", out string? accessToken))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },

                    OnTokenValidated = context =>
                    {
                        // 1. Service token: u=svc, bỏ qua kiểm tra session, kiểm tra sub là GUID hợp lệ
                        if (context.Principal?.HasClaim("u", "svc") == true)
                        {
                            string? serviceSub = context.Principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                            if (string.IsNullOrWhiteSpace(serviceSub) || !Guid.TryParse(serviceSub, out Guid serviceId) || serviceId == Guid.Empty)
                            {
                                context.Fail("Token thiếu hoặc chứa thông tin định danh service không hợp lệ.");
                            }
                            return Task.CompletedTask;
                        }

                        // 2. User token: kiểm tra sub là GUID hợp lệ
                        string? userSub = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                        if (string.IsNullOrWhiteSpace(userSub) || !Guid.TryParse(userSub, out Guid userId) || userId == Guid.Empty)
                        {
                            context.Fail("Token thiếu hoặc chứa thông tin định danh người dùng không hợp lệ.");
                            return Task.CompletedTask;
                        }

                        // 3. User token: cần sid là GUID hợp lệ
                        string? sessionIdClaim = context.Principal?.FindFirst("sid")?.Value;
                        if (string.IsNullOrWhiteSpace(sessionIdClaim) || !Guid.TryParse(sessionIdClaim, out Guid sessionId) || sessionId == Guid.Empty)
                        {
                            context.Fail("Token thiếu hoặc chứa thông tin phiên đăng nhập không hợp lệ.");
                            return Task.CompletedTask;
                        }

                        return Task.CompletedTask;
                    },

                    OnChallenge = async context =>
                    {
                        if (context.Handled)
                        {
                            return;
                        }

                        context.HandleResponse();

                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/problem+json";

                        if (!context.Response.Headers.ContainsKey("WWW-Authenticate"))
                        {
                            context.Response.Headers.Append("WWW-Authenticate", "Bearer error=\"invalid_token\"");
                        }

                        var problem = new ProblemDetails
                        {
                            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                            Title = CatalogErrors.Catalog.Unauthorized.Code,
                            Status = StatusCodes.Status401Unauthorized,
                            // Không trả AuthenticateFailure.Message: thông điệp của thư viện JWT có thể lộ chi tiết token/khóa.
                            Detail = CatalogErrors.Catalog.Unauthorized.Message,
                            Instance = context.HttpContext.Request.Path
                        };
                        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                        problem.Extensions[CorrelationIdMiddleware.ProblemDetailsKey] = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);

                        await context.Response.WriteAsJsonAsync(problem);
                    },

                    OnForbidden = async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/problem+json";

                        var problem = new ProblemDetails
                        {
                            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                            Title = CatalogErrors.Catalog.Forbidden.Code,
                            Status = StatusCodes.Status403Forbidden,
                            Detail = CatalogErrors.Catalog.Forbidden.Message,
                            Instance = context.HttpContext.Request.Path
                        };
                        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                        problem.Extensions[CorrelationIdMiddleware.ProblemDetailsKey] = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);

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
                    RoleClaimType = "r"
                };
            });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }
}
