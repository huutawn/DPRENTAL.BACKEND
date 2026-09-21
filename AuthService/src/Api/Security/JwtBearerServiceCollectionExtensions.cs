using System.IdentityModel.Tokens.Jwt;
using Dprental.AuthService.Domain.Enums;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Api.Security;

/// <summary>Các extension đăng ký JWT authentication vào dependency injection container.</summary>
internal static class JwtBearerServiceCollectionExtensions
{
    /// <summary>Tên claim permission rút gọn ("p"), phải khớp <c>RsaAccessTokenIssuer</c>.</summary>
    private const string PermissionClaimType = JwtClaimCatalog.Claims.Permission;

    /// <summary>Đăng ký JWT Bearer authentication cho API bằng key ring của Application.</summary>
    internal static IServiceCollection AddJwtBearerSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddAuthorization(options =>
        {
            // Một policy cho mỗi permission chuẩn: yêu cầu claim "p" chứa đúng alias ngắn.
            foreach (string permission in PermissionCode.All)
            {
                string alias = JwtClaimCatalog.PermissionAliases.ToAlias(permission);
                options.AddPolicy(permission, policy => policy.RequireClaim(PermissionClaimType, alias));
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

        // Giữ tên claim nguyên bản (sub, r, p) để contract JWT không bị .NET tự ánh xạ.
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
            }
        };
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            // Chỉ xác thực bằng public key có kid trùng JWT header; không thử tuần tự các key khác.
            IssuerSigningKeyResolver = (_, _, keyId, _) => keyRing.ResolveVerificationKeys(keyId),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = JwtClaimCatalog.Claims.Role
        };
    }
}
