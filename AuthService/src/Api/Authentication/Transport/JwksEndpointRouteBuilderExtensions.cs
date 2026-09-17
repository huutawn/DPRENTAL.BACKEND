using Dprental.AuthService.Api.Authentication.Infrastructure;

namespace Dprental.AuthService.Api.Authentication.Transport;

internal static class JwksEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapJwksEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/jwks.json", (HttpContext context, RsaJwtKeyRing keyRing) =>
            {
                context.Response.Headers.CacheControl = "public, max-age=300";
                return Results.Json(keyRing.CreateJwks());
            })
            .AllowAnonymous()
            .WithName("GetJwtJsonWebKeySet");

        return endpoints;
    }
}
