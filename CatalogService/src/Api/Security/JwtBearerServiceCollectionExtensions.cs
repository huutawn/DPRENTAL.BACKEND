using System.Security.Claims;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.CatalogService.Api.Security;

/// <summary>Các extension đăng ký JWT Bearer authentication vào dependency injection container.</summary>
internal static partial class JwtBearerServiceCollectionExtensions
{
    /// <summary>Tên HttpClient dùng riêng để tải JWKS từ AuthService.</summary>
    internal const string JwksHttpClientName = "auth-jwks";

    internal static IServiceCollection AddJwtBearerSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();

        services.AddHttpClient(JwksHttpClientName, (serviceProvider, client) =>
            client.Timeout = TimeSpan.FromSeconds(serviceProvider.GetRequiredService<IOptions<JwtOptions>>().Value.TimeoutSeconds));
        services.AddSingleton(serviceProvider => new JwksConfigurationManager(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(JwksHttpClientName),
            serviceProvider.GetRequiredService<IOptions<JwtOptions>>().Value,
            serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksConfigurationManager, IOptions<JwtOptions>>((bearerOptions, jwksManager, jwtOptionsAccessor) =>
            {
                JwtOptions jwtOptions = jwtOptionsAccessor.Value;

                // Giữ tên claim nguyên bản (sub, r, sid, u)
                bearerOptions.MapInboundClaims = false;
                bearerOptions.ConfigurationManager = jwksManager;
                bearerOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = async context =>
                    {
                        // Ưu tiên Authorization header; fallback sang cookie accessToken
                        string? authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(authHeader) &&
                            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Token = authHeader["Bearer ".Length..].Trim();
                        }
                        else if (string.IsNullOrWhiteSpace(context.Token) &&
                            context.Request.Cookies.TryGetValue("accessToken", out string? accessToken))
                        {
                            context.Token = accessToken;
                        }

                        if (string.IsNullOrWhiteSpace(context.Token))
                        {
                            return;
                        }

                        if (!new JsonWebTokenHandler().CanReadToken(context.Token))
                        {
                            context.Fail("Token không đúng định dạng JWT.");
                            return;
                        }

                        try
                        {
                            // kid chưa xác thực chỉ dùng để chọn public key; claims vẫn chưa được tin cho tới khi verify chữ ký.
                            string keyId = new JsonWebToken(context.Token).Kid;
                            if (!await jwksManager.EnsureSigningKeyAsync(keyId, context.HttpContext.RequestAborted))
                            {
                                context.Fail("Token được ký bằng key không xác định.");
                            }
                        }
                        catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
                        {
                            // AuthService không truy cập được: từ chối token thay vì trả 500, endpoint anonymous vẫn phục vụ bình thường.
                            LogJwksUnavailable(
                                context.HttpContext.RequestServices.GetRequiredService<ILogger<JwksConfigurationManager>>(), ex);
                            context.Fail("Signing key của AuthService hiện không khả dụng.");
                        }
                    },

                    OnTokenValidated = context =>
                    {
                        // 1. Service token: u=svc, bỏ qua kiểm tra session, kiểm tra sub là GUID hợp lệ
                        if (context.Principal?.HasClaim(CatalogClaims.Types.TokenUse, CatalogClaims.TokenUses.Service) == true)
                        {
                            string? serviceSub = context.Principal.FindFirst(CatalogClaims.Types.Subject)?.Value;
                            if (string.IsNullOrWhiteSpace(serviceSub) || !Guid.TryParse(serviceSub, out Guid serviceId) || serviceId == Guid.Empty)
                            {
                                context.Fail("Token thiếu hoặc chứa thông tin định danh service không hợp lệ.");
                            }
                            return Task.CompletedTask;
                        }

                        // 2. User token: kiểm tra sub là GUID hợp lệ
                        string? userSub = context.Principal?.FindFirst(CatalogClaims.Types.Subject)?.Value;
                        if (string.IsNullOrWhiteSpace(userSub) || !Guid.TryParse(userSub, out Guid userId) || userId == Guid.Empty)
                        {
                            context.Fail("Token thiếu hoặc chứa thông tin định danh người dùng không hợp lệ.");
                            return Task.CompletedTask;
                        }

                        // 3. User token: cần sid là GUID hợp lệ
                        string? sessionIdClaim = context.Principal?.FindFirst(CatalogClaims.Types.SessionId)?.Value;
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
                    // Chỉ dùng đúng key khớp kid trong JWKS, không thử lần lượt mọi key.
                    TryAllIssuerSigningKeys = false,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = CatalogClaims.Types.Subject,
                    RoleClaimType = CatalogClaims.Types.Role
                };
            });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(CatalogPolicies.CategoryManage, policy => policy
                .RequireAssertion(context => IsUserInAnyRole(context.User, CatalogClaims.Roles.Admin)));
            options.AddPolicy(CatalogPolicies.EquipmentManage, policy => policy
                .RequireAssertion(context => IsUserInAnyRole(context.User, CatalogClaims.Roles.Admin)));
            options.AddPolicy(CatalogPolicies.BrandManage, policy => policy
                .RequireAssertion(context => IsUserInAnyRole(context.User, CatalogClaims.Roles.Admin)));
        });

        return services;
    }

    /// <summary>Role chỉ có nghĩa với user token; service token (<c>u = svc</c>) không được quản lý catalog.</summary>
    private static bool IsUserInAnyRole(ClaimsPrincipal user, params string[] roles) =>
        user.Identity?.IsAuthenticated == true
        && !user.HasClaim(CatalogClaims.Types.TokenUse, CatalogClaims.TokenUses.Service)
        && roles.Any(user.IsInRole);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không tải được JWKS từ AuthService để xác thực token.")]
    private static partial void LogJwksUnavailable(ILogger logger, Exception exception);
}
