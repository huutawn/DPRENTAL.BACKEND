using System.Security.Claims;

namespace Dprental.PaymentService.Api.Security;

public static class PaymentClaimsPrincipalExtensions
{
    public static bool HasServiceAudience(this ClaimsPrincipal user, string audience)
    {
        return user.FindAll("aud").Any(c => string.Equals(c.Value, audience, StringComparison.Ordinal));
    }

    public static bool HasClientAudience(this ClaimsPrincipal user, string audience)
    {
        return user.FindAll("aud").Any(c => string.Equals(c.Value, audience, StringComparison.Ordinal));
    }

    public static bool IsServiceToken(this ClaimsPrincipal user)
    {
        return string.Equals(user.FindFirst("u")?.Value, "svc", StringComparison.Ordinal);
    }

    public static bool IsPlatformAdmin(this ClaimsPrincipal user)
    {
        if (user.IsServiceToken()) return false;
        return user.FindAll("r").Concat(user.FindAll("role"))
            .Any(c => c.Value is "adm" or "adp" or "admin" or "admin_platform");
    }

    public static bool HasScope(this ClaimsPrincipal user, string scope)
    {
        var scopes = user.FindAll("s")
            .Concat(user.FindAll("scope"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return scopes.Any(s => string.Equals(s, scope, StringComparison.Ordinal));
    }

    public static bool TryGetSubjectUserId(this ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        string? sub = user.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out userId);
    }
}
