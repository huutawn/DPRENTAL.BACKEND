using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Dprental.MediaService.Api.Security;

public class CurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public string? UserId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                // Nếu là service token (token_use = svc hoặc service), không coi sub là userId của người dùng thật
                bool isServiceToken = user.HasClaim("u", "svc") ||
                                     user.HasClaim("token_use", "service");

                if (!isServiceToken)
                {
                    string? sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ??
                                 user.FindFirst("sub")?.Value ??
                                 user.FindFirst("user_id")?.Value;

                    if (!string.IsNullOrWhiteSpace(sub))
                    {
                        return sub;
                    }
                }
            }

            if (_httpContextAccessor.HttpContext?.Request.Headers.TryGetValue("X-User-Id", out var userIdHeader) == true &&
                !string.IsNullOrWhiteSpace(userIdHeader))
            {
                return userIdHeader.ToString();
            }

            return null;
        }
    }

    public string? ServiceName
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                string? service = user.FindFirst("service_name")?.Value ??
                                 user.FindFirst("client_id")?.Value;

                if (!string.IsNullOrWhiteSpace(service))
                {
                    return service;
                }

                // Nếu token có token_use = svc hoặc service thì claim sub chính là định danh của service
                if (user.HasClaim("u", "svc") || user.HasClaim("token_use", "service"))
                {
                    string? serviceSub = user.FindFirstValue(ClaimTypes.NameIdentifier) ??
                                         user.FindFirst("sub")?.Value;
                    if (!string.IsNullOrWhiteSpace(serviceSub))
                    {
                        return serviceSub;
                    }
                }
            }

            if (_httpContextAccessor.HttpContext?.Request.Headers.TryGetValue("X-Service-Name", out var serviceHeader) == true &&
                !string.IsNullOrWhiteSpace(serviceHeader))
            {
                return serviceHeader.ToString();
            }

            return null;
        }
    }

    public bool IsAuthenticated
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(UserId) || !string.IsNullOrWhiteSpace(ServiceName);
        }
    }
}
