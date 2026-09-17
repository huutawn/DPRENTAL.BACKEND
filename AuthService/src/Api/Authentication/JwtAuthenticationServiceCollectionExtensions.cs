using System.IdentityModel.Tokens.Jwt;
using Dprental.AuthService.Api.Authentication.Configuration;
using Dprental.AuthService.Api.Authentication.Infrastructure;
using Dprental.AuthService.Application.Authentication.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Api.Authentication;

/// <summary>Các extension đăng ký JWT authentication vào dependency injection container.</summary>
internal static class JwtAuthenticationServiceCollectionExtensions
{
    /// <summary>Đăng ký key ring, token issuer và JWT Bearer authentication cho API.</summary>
    internal static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateOnStart bảo đảm API không chạy với issuer, audience hoặc RSA key chưa hợp lệ.
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetRequiredSection(JwtOptions.SectionName))
            .ValidateOnStart();

        // TimeProvider được inject để issuer không phụ thuộc trực tiếp vào đồng hồ hệ thống khi kiểm thử.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RsaJwtKeyRing>();
        services.AddSingleton<IAccessTokenIssuer, RsaAccessTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddAuthorization();

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
            RoleClaimType = "role"
        };
    }
}
