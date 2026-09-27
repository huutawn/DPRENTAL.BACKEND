using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Dprental.MediaService.Api.Security;

public sealed class CurrentUserContext(IHttpContextAccessor httpContextAccessor, IOptions<JwtOptions> options) : ICurrentUserContext
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    private bool IsService => Principal?.HasClaim("u", "svc") == true;

    public string? UserId => Principal?.Identity?.IsAuthenticated == true && !IsService
        ? Principal.FindFirstValue("sub")
        : null;

    public string? ServiceName
    {
        get
        {
            if (Principal?.Identity?.IsAuthenticated != true || !IsService || !Principal.HasClaim("s", "media.verify"))
            {
                return null;
            }

            string? subject = Principal.FindFirstValue("sub");
            return subject is not null && options.Value.ServicePrincipals.TryGetValue(subject, out string? serviceName)
                ? serviceName
                : null;
        }
    }

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}
