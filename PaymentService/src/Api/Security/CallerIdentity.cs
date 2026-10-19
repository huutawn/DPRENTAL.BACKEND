using System.Security.Claims;
using Dprental.PaymentService.Application.IServices;

namespace Dprental.PaymentService.Api.Security;

public static class CallerIdentity
{
    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId) =>
        user.TryGetSubjectUserId(out userId) && userId != Guid.Empty;

    public static bool TryGetCaller(ClaimsPrincipal user, out CallerContext caller)
    {
        if (user.TryGetSubjectUserId(out var id) && id != Guid.Empty)
        {
            caller = new CallerContext(id, user.IsPlatformAdmin());
            return true;
        }
        caller = new CallerContext(Guid.Empty, false);
        return false;
    }
}
