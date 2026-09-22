using Dprental.Gateway.Infrastructure.Security.Middlewares;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Dprental.Gateway.Infrastructure.Security.Transforms;

/// <summary>
/// Loại bỏ CORS response headers từ downstream trước khi Gateway áp dụng policy CORS ở ranh giới public.
/// </summary>
public sealed class GatewayCorsResponseTransformProvider : ITransformProvider
{
    /// <inheritdoc />
    public void ValidateRoute(TransformRouteValidationContext context)
    {
    }

    /// <inheritdoc />
    public void ValidateCluster(TransformClusterValidationContext context)
    {
    }

    /// <inheritdoc />
    public void Apply(TransformBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddResponseTransform(transformContext =>
        {
            GatewayCorsResponseHeaders.Remove(transformContext.HttpContext.Response.Headers);
            return ValueTask.CompletedTask;
        });
    }
}
