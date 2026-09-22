using Dprental.AuthService.Domain.Utils;

namespace Dprental.AuthService.Api.Endpoints;

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
            .WithName("GetJwtJsonWebKeySet")
            .WithSummary("Công bố JSON Web Key Set để consumer xác thực access token RS256.");

        return endpoints;
    }
}
