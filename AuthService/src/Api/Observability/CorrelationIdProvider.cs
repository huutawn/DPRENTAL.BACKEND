using Dprental.AuthService.Application.IServices;

namespace Dprental.AuthService.Api.Observability;

/// <summary>Cung cấp correlation ID của request hiện tại cho các use case ghi audit log.</summary>
/// <param name="httpContextAccessor">Accessor dùng để đọc header của HTTP request hiện tại.</param>
public class CorrelationIdProvider(IHttpContextAccessor httpContextAccessor) : ICorrelationIdProvider
{
    /// <summary>Lấy correlation ID từ header hoặc sinh ID mới khi request không cung cấp.</summary>
    public string GetCorrelationId()
    {
        return httpContextAccessor.HttpContext?.Request.Headers["X-Correlation-ID"].FirstOrDefault()
               ?? Guid.NewGuid().ToString();
    }
}
