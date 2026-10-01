using Dprental.CatalogService.Application.IServices;

namespace Dprental.CatalogService.Api.Observability;

/// <summary>Cung cấp correlation ID của request hiện tại.</summary>
public class CorrelationIdProvider(IHttpContextAccessor httpContextAccessor) : ICorrelationIdProvider
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    /// <summary>Lấy correlation ID ổn định từ Items (nếu đã gán trong middleware), từ header hoặc sinh ID mới.</summary>
    public string GetCorrelationId()
    {
        HttpContext? context = httpContextAccessor.HttpContext;
        if (context?.Items.TryGetValue(ItemKey, out object? itemVal) == true && itemVal is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        string? headerVal = context?.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerVal))
        {
            if (context != null)
            {
                context.Items[ItemKey] = headerVal;
            }
            return headerVal;
        }

        string newId = Guid.NewGuid().ToString();
        if (context != null)
        {
            context.Items[ItemKey] = newId;
        }
        return newId;
    }
}
