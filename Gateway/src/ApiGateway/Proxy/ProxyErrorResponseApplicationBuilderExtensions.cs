using Dprental.Gateway.Errors;
using Yarp.ReverseProxy.Forwarder;

namespace Dprental.Gateway.Proxy;

/// <summary>Maps YARP transport failures to Gateway-owned errors while preserving normal upstream responses.</summary>
internal static class ProxyErrorResponseApplicationBuilderExtensions
{
    /// <summary>Adds error translation after YARP has attempted to forward a request.</summary>
    internal static IApplicationBuilder UseGatewayProxyErrorResponses(this IApplicationBuilder builder)
    {
        return builder.Use(async (context, next) =>
        {
            await next(context);

            IForwarderErrorFeature? failure = context.GetForwarderErrorFeature();
            if (failure is null || failure.Error == ForwarderError.None || context.Response.HasStarted)
            {
                return;
            }

            if (failure.Error == ForwarderError.RequestCanceled && context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            (int statusCode, GatewayError error) = failure.Error is ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout
                ? (StatusCodes.Status504GatewayTimeout, GatewayError.UpstreamTimeout)
                : (StatusCodes.Status502BadGateway, GatewayError.UpstreamUnavailable);
            await GatewayErrorResponseWriter.WriteAsync(context, statusCode, error, context.RequestAborted);
        });
    }
}
