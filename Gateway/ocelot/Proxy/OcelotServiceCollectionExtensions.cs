using System;
using System.Collections.Generic;
using System.Linq;
using Dprental.Gateway.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ocelot.DependencyInjection;

namespace Dprental.Gateway.Proxy;

/// <summary>
/// Đăng ký và cấu hình proxy Ocelot 25.0 cho API Gateway.
/// </summary>
public static class OcelotServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký Ocelot và các delegating handlers cho bảo mật danh tính, correlation, origin và CORS.
    /// </summary>
    /// <param name="services">Tập hợp dịch vụ DI container.</param>
    /// <param name="configuration">Cấu hình hệ thống (IConfiguration).</param>
    /// <returns>Tập hợp dịch vụ IServiceCollection để xâu chuỗi.</returns>
    public static IServiceCollection AddGatewayOcelot(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddOcelot(configuration)
            .AddDelegatingHandler<TrustedIdentityDelegatingHandler>(global: true)
            .AddDelegatingHandler<GatewayCorsDelegatingHandler>(global: true);

        return services;
    }

    /// <summary>
    /// Kiểm tra cấu hình Ocelot sau khi các nguồn cấu hình đã được nạp.
    /// Đảm bảo timeout dương và mọi route có đường dẫn, scheme, downstream host/port.
    /// </summary>
    /// <param name="configuration">Cấu hình hệ thống.</param>
    internal static void ValidateOcelotConfiguration(IConfiguration configuration)
    {
        string? timeoutValue = configuration["GlobalConfiguration:Timeout"];
        if (!int.TryParse(timeoutValue, out int timeoutSeconds) || timeoutSeconds <= 0)
        {
            throw new InvalidOperationException("GlobalConfiguration:Timeout must be configured and greater than 0.");
        }

        IConfigurationSection routesSection = configuration.GetSection("Routes");
        List<IConfigurationSection> routes = routesSection.GetChildren().ToList();

        if (routes.Count == 0)
        {
            throw new InvalidOperationException("Routes must contain at least one configured upstream route.");
        }

        foreach (IConfigurationSection route in routes)
        {
            string? upstreamPathTemplate = route["UpstreamPathTemplate"];
            string? downstreamPathTemplate = route["DownstreamPathTemplate"];
            string? downstreamScheme = route["DownstreamScheme"];

            if (string.IsNullOrWhiteSpace(upstreamPathTemplate))
            {
                throw new InvalidOperationException("Each route must define an UpstreamPathTemplate.");
            }

            if (string.IsNullOrWhiteSpace(downstreamPathTemplate))
            {
                throw new InvalidOperationException($"Route '{upstreamPathTemplate}' must define a DownstreamPathTemplate.");
            }

            if (string.IsNullOrWhiteSpace(downstreamScheme))
            {
                throw new InvalidOperationException($"Route '{upstreamPathTemplate}' must define a DownstreamScheme.");
            }

            IConfigurationSection hostAndPortsSection = route.GetSection("DownstreamHostAndPorts");
            List<IConfigurationSection> hostAndPorts = hostAndPortsSection.GetChildren().ToList();

            if (hostAndPorts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Route '{upstreamPathTemplate}' must configure at least one DownstreamHostAndPorts entry.");
            }

            foreach (IConfigurationSection hostAndPort in hostAndPorts)
            {
                string? host = hostAndPort["Host"];
                string? portValue = hostAndPort["Port"];

                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new InvalidOperationException(
                        $"Route '{upstreamPathTemplate}' contains a DownstreamHostAndPorts entry without a Host.");
                }

                if (!int.TryParse(portValue, out int port) || port <= 0 || port > 65535)
                {
                    throw new InvalidOperationException(
                        $"Route '{upstreamPathTemplate}' contains an invalid Port '{portValue}'.");
                }
            }
        }
    }
}
