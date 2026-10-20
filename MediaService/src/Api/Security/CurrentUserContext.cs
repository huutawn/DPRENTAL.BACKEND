using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Dprental.MediaService.Api.Security;

public sealed class CurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public string? UserId => Principal?.Identity?.IsAuthenticated == true && !Principal.HasClaim("u", "svc")
        ? Principal.FindFirstValue("sub")
        : null;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}
