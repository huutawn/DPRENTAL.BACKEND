
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Security;

/// <summary>
/// Cung cấp các phương thức mở rộng (Extension Methods) để cấu hình và đăng ký các dịch vụ
/// bảo mật Gateway vào Dependency Injection (DI) container.
/// </summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký toàn bộ các dịch vụ bảo mật của Gateway, bao gồm CORS, Header Sanitization,
    /// Request Size Protection, Security Headers và Trusted Identity Forwarding.
    /// </summary>
    /// <param name="services">Tập hợp dịch vụ DI container.</param>
    /// <param name="configuration">Cấu hình hệ thống (IConfiguration).</param>
    /// <returns>Tập hợp dịch vụ IServiceCollection để xâu chuỗi cấu hình.</returns>
    public static IServiceCollection AddGatewaySecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 1. Cấu hình và thẩm định GatewaySecurityOptions
        services.AddSingleton<IValidateOptions<GatewaySecurityOptions>, GatewaySecurityOptionsValidator>();
        services.AddOptions<GatewaySecurityOptions>()
            .Bind(configuration.GetSection(GatewaySecurityOptions.SectionName))
            .ValidateOnStart();

        // 2. Đăng ký IHttpContextAccessor để hỗ trợ truy xuất ngữ cảnh
        services.AddHttpContextAccessor();

        // 3. Đăng ký các thành phần nghiệp vụ làm sạch và trích xuất danh tính
        services.AddSingleton<HeaderSanitizer>();
        services.AddSingleton<IdentityExtractor>();
        services.AddSingleton<IGatewayRateLimiter, GatewayRateLimiter>();

        // 4. Cấu hình ForwardedHeadersOptions của ASP.NET Core
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((options, secOptions) =>
            {
                ForwardedHeadersConfiguration.Configure(options, secOptions.Value);
            });

        return services;
    }
}
