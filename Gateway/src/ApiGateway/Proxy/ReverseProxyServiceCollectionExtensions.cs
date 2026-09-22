using Dprental.Gateway.Infrastructure.Security.Transforms;

namespace Dprental.Gateway.Proxy;

/// <summary>Đăng ký và cấu hình Reverse Proxy YARP cho API Gateway.</summary>
internal static class ReverseProxyServiceCollectionExtensions
{
    /// <summary>Nạp cấu hình các tuyến đường (routes), cụm máy chủ (clusters) và đăng ký bộ chuyển đổi danh tính đáng tin cậy.</summary>
    internal static IServiceCollection AddGatewayReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetRequiredSection("ReverseProxy"))
            .AddTransforms<TrustedIdentityTransformProvider>()
            .AddTransforms<GatewayCorsResponseTransformProvider>();

        return services;
    }
}
