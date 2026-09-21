using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Net.Http.Headers;

namespace Dprental.Gateway.Errors;

/// <summary>Writes canonical responses for authorization outcomes produced by protected Gateway routes.</summary>
internal sealed class GatewayAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <inheritdoc />
    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Challenged)
        {
            bool suppliedCredentials = context.Request.Headers.ContainsKey(HeaderNames.Authorization) ||
                                       context.Request.Cookies.ContainsKey("accessToken");
            context.Response.Headers[HeaderNames.WWWAuthenticate] = suppliedCredentials
                ? "Bearer error=\"invalid_token\""
                : "Bearer";
            return GatewayErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                suppliedCredentials ? GatewayError.InvalidAccessToken : GatewayError.AuthenticationRequired,
                context.RequestAborted);
        }

        return authorizeResult.Forbidden
            ? GatewayErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                GatewayError.AccessForbidden,
                context.RequestAborted)
            : next(context);
    }
}
