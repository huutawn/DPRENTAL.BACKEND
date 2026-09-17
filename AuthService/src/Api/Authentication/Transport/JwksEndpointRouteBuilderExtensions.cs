using Dprental.AuthService.Api.Authentication.Infrastructure;

namespace Dprental.AuthService.Api.Authentication.Transport;

/// <summary>Các extension công bố JSON Web Key Set cho consumer bên ngoài.</summary>
internal static class JwksEndpointRouteBuilderExtensions
{
    /// <summary>Đăng ký endpoint JWKS công khai để consumer tải public key xác thực access token.</summary>
    internal static IEndpointRouteBuilder MapJwksEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/jwks.json", (HttpContext context, RsaJwtKeyRing keyRing) =>
            {
                // Cache ngắn giúp consumer nhận key mới trong quá trình rotation mà không tải JWKS mọi request.
                context.Response.Headers.CacheControl = "public, max-age=300";
                return Results.Json(keyRing.CreateJwks());
            })
            .AllowAnonymous()
            .WithName("GetJwtJsonWebKeySet");

        return endpoints;
    }
}
