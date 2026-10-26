using Dprental.AnalyticsService.Application.Hub.Options;
using Dprental.AnalyticsService.Application.Hub.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub.Extensions;

public static class AnalyticsHubEndpointRouteBuilderExtensions
{
    public static IApplicationBuilder UseAnalyticsHubOriginGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<AnalyticsHubOriginGuardMiddleware>();

    public static HubEndpointConventionBuilder MapAnalyticsHub(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        AnalyticsHubOptions options = endpoints.ServiceProvider.GetRequiredService<IOptions<AnalyticsHubOptions>>().Value;

        return endpoints.MapHub<AnalyticsHub>(options.HubPath, hub =>
        {
            hub.Transports = HttpTransportType.WebSockets;   // Chỉ WebSocket, giống PaymentHub
            hub.CloseOnAuthenticationExpiration = true;      // Token hết hạn → ngắt kết nối
        });
    }
}
