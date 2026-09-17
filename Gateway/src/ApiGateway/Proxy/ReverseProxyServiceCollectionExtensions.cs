namespace Dprental.Gateway.Proxy;

/// <summary>Registers the Gateway's single YARP configuration source.</summary>
internal static class ReverseProxyServiceCollectionExtensions
{
    /// <summary>Loads routes and clusters from standard ASP.NET Core configuration providers.</summary>
    internal static IServiceCollection AddGatewayReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetRequiredSection("ReverseProxy"));

        return services;
    }
}
