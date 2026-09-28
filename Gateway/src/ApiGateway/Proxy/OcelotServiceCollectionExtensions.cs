using System;
using System.Collections.Generic;
using System.Linq;
using Dprental.Gateway.Infrastructure.Security.DelegatingHandlers;
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
            throw new InvalidOperationException(
                "Cấu hình Ocelot không hợp lệ: GlobalConfiguration:Timeout phải là số giây dương.");
        }

        IConfigurationSection routesSection = configuration.GetSection("Routes");
        List<IConfigurationSection> routes = routesSection.GetChildren().ToList();

        if (routes.Count == 0)
        {
            throw new InvalidOperationException(
                "Cấu hình Ocelot không hợp lệ: Không tìm thấy tuyến đường nào trong section 'Routes'.");
        }

        foreach (IConfigurationSection route in routes)
        {
            string routeKey = route["Key"] ?? route.Key;

            string? upstreamPath = route["UpstreamPathTemplate"];
            if (string.IsNullOrWhiteSpace(upstreamPath) || !upstreamPath.StartsWith('/'))
            {
                throw new InvalidOperationException(
                    $"Cấu hình Ocelot không hợp lệ cho route '{routeKey}': UpstreamPathTemplate phải bắt đầu bằng '/'.");
            }

            string? downstreamPath = route["DownstreamPathTemplate"];
            if (string.IsNullOrWhiteSpace(downstreamPath) || !downstreamPath.StartsWith('/'))
            {
                throw new InvalidOperationException(
                    $"Cấu hình Ocelot không hợp lệ cho route '{routeKey}': DownstreamPathTemplate phải bắt đầu bằng '/'.");
            }

            string? scheme = route["DownstreamScheme"];
            if (string.IsNullOrWhiteSpace(scheme))
            {
                throw new InvalidOperationException(
                    $"Cấu hình Ocelot không hợp lệ cho route '{routeKey}': DownstreamScheme không được để trống.");
            }

            List<IConfigurationSection> hostAndPorts = route.GetSection("DownstreamHostAndPorts").GetChildren().ToList();
            if (hostAndPorts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Cấu hình Ocelot không hợp lệ cho route '{routeKey}': DownstreamHostAndPorts không được để trống.");
            }

            foreach (IConfigurationSection hostAndPort in hostAndPorts)
            {
                string? host = hostAndPort["Host"];
                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new InvalidOperationException(
                        $"Cấu hình Ocelot không hợp lệ cho route '{routeKey}': Downstream host không được để trống.");
                }

                if (!int.TryParse(hostAndPort["Port"], out int port) || port <= 0)
                {
                    throw new InvalidOperationException(
                        $"Cấu hình Ocelot không hợp lệ cho route '{routeKey}': Downstream port '{hostAndPort["Port"]}' không hợp lệ.");
                }
            }
        }
    }
}
