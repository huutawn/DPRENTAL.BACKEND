using System.Security.Claims;

namespace Dprental.MediaService.Api.Security;

public sealed class CurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    private bool IsService => Principal?.HasClaim("u", "svc") == true;

    public string? UserId => Principal?.Identity?.IsAuthenticated == true && !IsService
        ? Principal.FindFirstValue("sub")
        : null;

    public string? ServiceId => Principal?.Identity?.IsAuthenticated == true && IsService
        ? Principal.FindFirstValue("sub")
        : null;

    public IReadOnlySet<string> ServiceScopes => Principal?.Identity?.IsAuthenticated == true && IsService
        ? Principal.FindAll("s").Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}
