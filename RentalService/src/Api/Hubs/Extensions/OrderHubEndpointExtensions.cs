using Dprental.RentalService.Api.Hubs.Security;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Api.Hubs.Extensions;

/// <summary>
/// Mở rộng IEndpointRouteBuilder và IApplicationBuilder cấu hình route và middleware cho OrderHub.
/// </summary>
public static class OrderHubEndpointExtensions
{
    public static IApplicationBuilder UseOrderHubOriginGuard(this IApplicationBuilder app)
    {
        return app.UseMiddleware<OrderHubOriginGuardMiddleware>();
    }

    public static HubEndpointConventionBuilder MapOrderHub(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<IOptions<OrderHubOptions>>()?.Value
            ?? new OrderHubOptions();

        string hubPath = string.IsNullOrWhiteSpace(options.HubPath)
            ? OrderHubOptions.DefaultHubPath
            : options.HubPath;

        return endpoints.MapHub<OrderHub>(hubPath, hubOptions =>
        {
            hubOptions.Transports = HttpTransportType.WebSockets;
            hubOptions.CloseOnAuthenticationExpiration = true;
        }).RequireAuthorization(OrderHubSecurityConstants.PolicyName);
    }
}
