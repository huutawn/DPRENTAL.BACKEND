using Dprental.AuthService.Application.IServices;

namespace Dprental.AuthService.Api.Observability;

public class CorrelationIdProvider(IHttpContextAccessor httpContextAccessor) : ICorrelationIdProvider
{
    public string GetCorrelationId()
    {
        return httpContextAccessor.HttpContext?.Request.Headers["X-Correlation-ID"].FirstOrDefault()
               ?? Guid.NewGuid().ToString();
    }
}