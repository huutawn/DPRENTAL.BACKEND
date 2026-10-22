using Dprental.PaymentService.Application.Hub.Options;
using Dprental.PaymentService.Application.Hub.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Hub.Extensions;

public static class PaymentHubEndpointRouteBuilderExtensions
{
    public static IApplicationBuilder UsePaymentHubOriginGuard(this IApplicationBuilder app)
    {
        return app.UseMiddleware<PaymentHubOriginGuardMiddleware>();
    }

    public static HubEndpointConventionBuilder MapPaymentHub(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<PaymentHubOptions>>().Value;
        string path = string.IsNullOrWhiteSpace(options.HubPath) ? PaymentHubOptions.DefaultHubPath : options.HubPath;

        return endpoints.MapHub<PaymentHub>(path, hubOptions =>
        {
            hubOptions.Transports = HttpTransportType.WebSockets;
            hubOptions.CloseOnAuthenticationExpiration = true;
        });
    }
}
